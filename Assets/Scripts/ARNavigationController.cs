using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using TMPro;

/// <summary>
/// Minimal Indoor AR Navigation Controller.
/// Stripped of all GPS, drift nudging, and complex course-correction modules.
/// Pure workflow:
/// 1. Current Room Location (Dropdown): sets ground-truth start position.
/// 2. Destination Room Location (Dropdown): sets target destination.
/// 3. Reset Yaw Button: aligns camera facing direction with the corridor axis.
/// 4. Floor line guidance via LineRenderer on NavMesh.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class ARNavigationController : MonoBehaviour
{
    [Header("AR & Virtual World References")]
    [Tooltip("The XR Origin / AR Session Origin root Transform.")]
    public Transform xrOrigin;

    [Tooltip("The AR Camera.")]
    public Camera arCamera;

    [Tooltip("Parent transform containing room waypoints (e.g. [Destinations]).")]
    public Transform destinationsParent;

    [Header("UI References")]
    [Tooltip("Dropdown to select current starting room.")]
    public TMP_Dropdown currentRoomDropdown;

    [Tooltip("Dropdown to select destination room.")]
    public TMP_Dropdown destinationDropdown;

    [Tooltip("Status text for navigation feedback.")]
    public TextMeshProUGUI statusText;

    [Header("Navigation Settings")]
    [Tooltip("Frequency in seconds to recalculate NavMesh path.")]
    public float pathUpdateInterval = 0.3f;

    [Tooltip("Distance threshold (meters) for destination arrival.")]
    public float arrivalThreshold = 1.5f;

    [Tooltip("Elevation offset (meters) above floor surface to prevent z-fighting.")]
    public float lineElevation = 0.05f;

    [Header("Heading / Yaw Alignment")]
    [Tooltip("Target yaw rotation in degrees in virtual space (default: 180° matching XR Origin).")]
    public float targetYawDegrees = 180f;

    // Internal runtime state
    private LineRenderer lineRenderer;
    private NavMeshPath navMeshPath;
    private List<Transform> roomNodes = new List<Transform>();
    private Transform currentStartRoom = null;
    private Transform currentTargetRoom = null;
    private bool isNavigating = false;
    private Coroutine navigationRoutine;

    private void Awake()
    {
        // Ensure GPS is stopped to prevent any background battery drain or interference
        if (Input.location.status == LocationServiceStatus.Running || Input.location.status == LocationServiceStatus.Initializing)
        {
            Input.location.Stop();
        }

        lineRenderer = GetComponent<LineRenderer>();
        if (lineRenderer != null)
        {
            lineRenderer.enabled = false;
        }

        navMeshPath = new NavMeshPath();
    }

    private void Start()
    {
        ResolveDependencies();
        PopulateRoomDropdowns();
    }

    private void ResolveDependencies()
    {
        if (arCamera == null)
        {
            GameObject camObj = GameObject.FindWithTag("MainCamera");
            if (camObj != null) arCamera = camObj.GetComponent<Camera>();
            if (arCamera == null) arCamera = Camera.main;
        }

        if (xrOrigin == null)
        {
            GameObject originObj = GameObject.Find("XR Origin");
            if (originObj != null) xrOrigin = originObj.transform;
            else xrOrigin = transform;
        }

        if (destinationsParent == null)
        {
            GameObject destObj = GameObject.Find("[Destinations]");
            if (destObj != null) destinationsParent = destObj.transform;
        }

        if (statusText == null)
        {
            GameObject textObj = GameObject.Find("DebugStatusText");
            if (textObj != null) statusText = textObj.GetComponent<TextMeshProUGUI>();
        }

        if (currentRoomDropdown == null)
        {
            GameObject startDrop = GameObject.Find("CurrentRoomDropdown");
            if (startDrop == null) startDrop = GameObject.Find("DebugDropdown");
            if (startDrop != null) currentRoomDropdown = startDrop.GetComponent<TMP_Dropdown>();
        }

        if (destinationDropdown == null)
        {
            GameObject destDrop = GameObject.Find("DestinationDropdown");
            if (destDrop != null) destinationDropdown = destDrop.GetComponent<TMP_Dropdown>();
        }
    }

    private void PopulateRoomDropdowns()
    {
        if (destinationsParent == null) return;

        roomNodes.Clear();
        List<string> startOptions = new List<string> { "-- Select Current Room --" };
        List<string> destOptions = new List<string> { "-- Select Destination --" };

        foreach (Transform child in destinationsParent)
        {
            roomNodes.Add(child);
            startOptions.Add(child.name);
            destOptions.Add(child.name);
        }

        if (currentRoomDropdown != null)
        {
            currentRoomDropdown.ClearOptions();
            currentRoomDropdown.AddOptions(startOptions);
            currentRoomDropdown.onValueChanged.RemoveAllListeners();
            currentRoomDropdown.onValueChanged.AddListener(OnCurrentRoomSelected);
        }

        if (destinationDropdown != null)
        {
            destinationDropdown.ClearOptions();
            destinationDropdown.AddOptions(destOptions);
            destinationDropdown.onValueChanged.RemoveAllListeners();
            destinationDropdown.onValueChanged.AddListener(OnDestinationSelected);
        }

        SetStatus("1. Face hallway direction & select Current Room (sets position & 180° yaw).\n2. Or tap 'Reset Yaw' anytime while facing that way.\n3. Select Destination to navigate.");
    }

    /// <summary>
    /// Called when the user picks their current starting room.
    /// Snaps XR Origin so the camera sits directly at that room's coordinates,
    /// and aligns yaw to targetYawDegrees (180°).
    /// </summary>
    public void OnCurrentRoomSelected(int index)
    {
        if (index <= 0 || index - 1 >= roomNodes.Count)
        {
            currentStartRoom = null;
            SetStatus("Please select your current room location.");
            return;
        }

        currentStartRoom = roomNodes[index - 1];
        AlignWorldToRoom(currentStartRoom);

        // If destination is already picked, immediately re-route
        if (currentTargetRoom != null)
        {
            StartNavigation();
        }
    }

    /// <summary>
    /// Snaps the XR Origin container so that the camera matches the room's horizontal (X, Z) location,
    /// and rotates the origin around the camera to match targetYawDegrees (180°).
    /// </summary>
    private void AlignWorldToRoom(Transform room)
    {
        if (xrOrigin == null || arCamera == null || room == null) return;

        // 1. Move origin so camera is exactly at the room position
        Vector3 camOffset = arCamera.transform.position - xrOrigin.position;
        camOffset.y = 0f;

        Vector3 targetOrigin = room.position - camOffset;
        targetOrigin.y = xrOrigin.position.y; // Preserve floor elevation
        xrOrigin.position = targetOrigin;

        // 2. Align rotation to targetYawDegrees (180°) around the camera
        ApplyYawAlignment();

        SetStatus($"<color=green>Position set to [{room.name}] & Yaw aligned to {targetYawDegrees}°.</color>");
    }

    /// <summary>
    /// Aligns the camera facing direction with the target yaw degrees (default: 180°).
    /// Rotates XR Origin around the camera position without shifting the camera's location.
    /// </summary>
    public void ApplyYawAlignment()
    {
        if (xrOrigin == null || arCamera == null) return;

        Vector3 camForward = arCamera.transform.forward;
        camForward.y = 0f;
        if (camForward.sqrMagnitude < 0.001f) return;
        camForward.Normalize();

        // Calculate world forward direction for targetYawDegrees (e.g. 180° -> (0, 0, -1))
        Vector3 targetDirection = Quaternion.Euler(0f, targetYawDegrees, 0f) * Vector3.forward;

        float deltaYaw = Vector3.SignedAngle(camForward, targetDirection, Vector3.up);
        xrOrigin.RotateAround(arCamera.transform.position, Vector3.up, deltaYaw);

        SetStatus($"<color=green>Yaw Reset to {targetYawDegrees}°! (Correction: {deltaYaw:F1}°)</color>");

        if (isNavigating)
        {
            UpdatePathAndCheckArrival();
        }
    }

    /// <summary>
    /// UI Hook for 'Reset Yaw' button:
    /// </summary>
    public void ResetYaw()
    {
        ApplyYawAlignment();
    }

    /// <summary>
    /// Called when the user picks a target destination.
    /// </summary>
    public void OnDestinationSelected(int index)
    {
        if (index <= 0 || index - 1 >= roomNodes.Count)
        {
            CancelNavigation();
            return;
        }

        currentTargetRoom = roomNodes[index - 1];
        StartNavigation();
    }

    /// <summary>
    /// Begins real-time NavMesh path calculation and LineRenderer updates.
    /// </summary>
    public void StartNavigation()
    {
        if (currentTargetRoom == null) return;

        isNavigating = true;

        if (lineRenderer != null)
        {
            lineRenderer.enabled = true;
        }

        if (navigationRoutine != null)
        {
            StopCoroutine(navigationRoutine);
        }
        navigationRoutine = StartCoroutine(NavigationLoop());
    }

    /// <summary>
    /// Cancels active navigation and hides the line.
    /// </summary>
    public void CancelNavigation()
    {
        currentTargetRoom = null;
        isNavigating = false;

        if (navigationRoutine != null)
        {
            StopCoroutine(navigationRoutine);
            navigationRoutine = null;
        }

        if (lineRenderer != null)
        {
            lineRenderer.positionCount = 0;
            lineRenderer.enabled = false;
        }

        if (destinationDropdown != null && destinationDropdown.value != 0)
        {
            destinationDropdown.SetValueWithoutNotify(0);
        }

        if (currentStartRoom != null)
        {
            SetStatus($"Current Room: [{currentStartRoom.name}]. Select a destination.");
        }
        else
        {
            SetStatus("Select Current Room to begin.");
        }
    }

    private IEnumerator NavigationLoop()
    {
        WaitForSeconds wait = new WaitForSeconds(pathUpdateInterval);

        while (isNavigating && currentTargetRoom != null)
        {
            UpdatePathAndCheckArrival();
            yield return wait;
        }
    }

    private void UpdatePathAndCheckArrival()
    {
        if (arCamera == null || currentTargetRoom == null) return;

        Vector3 camPos = arCamera.transform.position;
        Vector3 targetPos = currentTargetRoom.position;

        // Arrival check on horizontal plane
        float distToTarget = Vector2.Distance(new Vector2(camPos.x, camPos.z), new Vector2(targetPos.x, targetPos.z));
        if (distToTarget <= arrivalThreshold)
        {
            SetStatus($"<color=green><b>Arrived at {currentTargetRoom.name}!</b></color>");
            CancelNavigation();
            return;
        }

        // Sample start and end positions onto NavMesh
        Vector3 startSample = camPos;
        if (NavMesh.SamplePosition(camPos, out NavMeshHit startHit, 3.0f, NavMesh.AllAreas))
        {
            startSample = startHit.position;
        }

        Vector3 endSample = targetPos;
        if (NavMesh.SamplePosition(endSample, out NavMeshHit endHit, 3.0f, NavMesh.AllAreas))
        {
            endSample = endHit.position;
        }

        // Calculate standard NavMesh path
        if (NavMesh.CalculatePath(startSample, endSample, NavMesh.AllAreas, navMeshPath))
        {
            if (navMeshPath.status == NavMeshPathStatus.PathComplete || navMeshPath.status == NavMeshPathStatus.PathPartial)
            {
                int cornerCount = navMeshPath.corners.Length;
                lineRenderer.positionCount = cornerCount;

                for (int i = 0; i < cornerCount; i++)
                {
                    Vector3 elevatedCorner = navMeshPath.corners[i];
                    elevatedCorner.y += lineElevation;
                    lineRenderer.SetPosition(i, elevatedCorner);
                }

                SetStatus($"Routing to: <b>{currentTargetRoom.name}</b>\nRemaining: {distToTarget:F1}m");
                return;
            }
        }

        // If path calculation failed
        if (lineRenderer != null)
        {
            lineRenderer.positionCount = 0;
        }
        SetStatus($"<color=orange>Routing to: {currentTargetRoom.name} (Calculating...)</color>");
    }

    private void SetStatus(string msg)
    {
        if (statusText != null)
        {
            statusText.text = msg;
        }
    }

    // =========================================================================
    // Backwards-Compatible Callbacks for existing Scene Buttons
    // =========================================================================

    public void TriggerManualMode()
    {
        if (currentRoomDropdown != null && currentRoomDropdown.value > 0)
        {
            OnCurrentRoomSelected(currentRoomDropdown.value);
        }
    }

    public void TriggerAutoMode()
    {
        // In minimal mode, instruct user to pick room directly
        SetStatus("Please select your Current Room from the dropdown.");
    }

    public void TriggerYawReset()
    {
        ResetYaw();
    }

    public void AssignAnchor()
    {
        TriggerManualMode();
    }

    public void DebugTriggerYawReset()
    {
        ResetYaw();
    }
}
