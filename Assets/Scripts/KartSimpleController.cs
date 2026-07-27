using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(Rigidbody))]
public class KartSimpleController : MonoBehaviour
{
    [Header("Riferimenti")]
    [SerializeField] private Transform kartModel;
    [SerializeField] private Transform groundCheck;

    [Header("Movimento")]
    [SerializeField] private float acceleration = 30f;
    [SerializeField] private float reverseAcceleration = 15f;
    [SerializeField] private float maxSpeed = 20f;
    [SerializeField] private float maxReverseSpeed = 8f;
    [SerializeField] private float steeringSpeed = 120f;
    [SerializeField] private float gravity = 20f;
    [SerializeField] private float dragWhenNotAccelerating = 2f;

    [Header("Aderenza")]
    [Tooltip("Quanto velocemente viene eliminato lo slittamento laterale nella guida normale.")]
    [SerializeField] private float normalLateralGrip = 10f;

    [Tooltip("Durante il drift deve essere più basso di Normal Lateral Grip.")]
    [SerializeField] private float driftLateralGrip = 2.5f;

    [Header("Controllo a terra")]
    [SerializeField] private LayerMask groundMask = ~0;
    [SerializeField] private float groundCheckRadius = 0.2f;
    [SerializeField] private float groundCheckDistance = 1.2f;
    [SerializeField] private float maximumGroundAngle = 60f;

    [Header("Visuale")]
    [SerializeField] private float steeringVisualAngle = 12f;
    [SerializeField] private float driftVisualAngle = 20f;

    [Tooltip("Tempo impiegato dal modello per raggiungere l'angolo visuale desiderato.")]
    [SerializeField] private float visualSteerSmoothTime = 0.12f;

    [Tooltip("Velocità con cui il modello si allinea alla normale del terreno.")]
    [SerializeField] private float groundAlignmentSpeed = 10f;

    [Header("Drift")]
    [SerializeField] private float minimumDriftSteer = 0.2f;
    [SerializeField] private float minimumDriftSpeed = 5f;
    [SerializeField] private float driftExitSpeed = 2f;
    [SerializeField] private bool requireAccelerationForDrift = true;

    [Tooltip("Sterzata minima mantenuta anche spingendo lo stick contro il drift.")]
    [Range(0f, 1f)]
    [SerializeField] private float minimumDriftSteeringMultiplier = 0.4f;

    [Tooltip("Sterzata massima quando lo stick accompagna il drift.")]
    [SerializeField] private float maximumDriftSteeringMultiplier = 1.15f;

    [Header("Hop visuale")]
    [SerializeField] private float hopHeight = 0.18f;
    [SerializeField] private float hopDuration = 0.28f;

    [Tooltip("Momento dell'hop in cui viene verificato l'ingresso in drift.")]
    [Range(0.1f, 0.9f)]
    [SerializeField] private float driftActivationPoint = 0.45f;

    [Header("VFX Drift")]
    [SerializeField] private List<ParticleSystem> leftDriftParticles = new();
    [SerializeField] private List<ParticleSystem> rightDriftParticles = new();

    private Rigidbody rb;

    private float steerInput;
    private float currentVisualYaw;
    private float visualYawVelocity;

    private bool accelerating;
    private bool reversing;
    private bool drifting;
    private bool driftButtonHeld;
    private bool inputSubscribed;
    private bool isGrounded;
    private bool isHopping;

    private int driftDirection;

    private Vector3 groundNormal = Vector3.up;
    private Vector3 smoothedGroundNormal = Vector3.up;

    private Quaternion initialModelRotation;
    private Vector3 initialModelPosition;

    private Coroutine hopCoroutine;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();

        rb.interpolation = RigidbodyInterpolation.Interpolate;

        // Impedisce alla fisica di far girare o ribaltare il kart.
        // La rotazione comandata viene applicata tramite MoveRotation.
        rb.constraints = RigidbodyConstraints.FreezeRotation;

        if (kartModel != null)
        {
            initialModelRotation = kartModel.localRotation;
            initialModelPosition = kartModel.localPosition;
        }
    }

    private void OnEnable()
    {
        SubscribeToInput();
    }

    private void Start()
    {
        SubscribeToInput();
        StopDriftParticles(true);
    }

    private void OnDisable()
    {
        UnsubscribeFromInput();

        steerInput = 0f;
        accelerating = false;
        reversing = false;
        drifting = false;
        driftButtonHeld = false;
        driftDirection = 0;

        if (hopCoroutine != null)
        {
            StopCoroutine(hopCoroutine);
            hopCoroutine = null;
        }

        isHopping = false;

        if (kartModel != null)
        {
            kartModel.localPosition = initialModelPosition;
        }

        StopDriftParticles(true);
    }

    private void Update()
    {
        if (!inputSubscribed)
        {
            SubscribeToInput();
        }

        UpdateVisuals();
    }

    private void FixedUpdate()
    {
        UpdateGroundState();

        if (drifting && ShouldEndDrift())
        {
            EndDrift();
        }

        ApplyGravity();
        ApplyAcceleration();
        ApplySteering();
        ApplyLateralGrip();
        LimitSpeed();

        rb.angularVelocity = Vector3.zero;
    }

    private bool ShouldEndDrift()
    {
        if (!isGrounded)
        {
            return true;
        }

        if (reversing)
        {
            return true;
        }

        if (GetForwardSpeed() < driftExitSpeed)
        {
            return true;
        }

        return false;
    }

    // ---------------------------------------------------------
    // INPUT
    // ---------------------------------------------------------

    private void SubscribeToInput()
    {
        if (inputSubscribed || InputManager.Instance == null)
        {
            return;
        }

        InputManager.Instance.OnSteer += HandleSteer;
        InputManager.Instance.OnAccelerateStarted += HandleAccelerateStarted;
        InputManager.Instance.OnAccelerateEnded += HandleAccelerateEnded;
        InputManager.Instance.OnReverseStarted += HandleReverseStarted;
        InputManager.Instance.OnReverseEnded += HandleReverseEnded;
        InputManager.Instance.OnDriftStarted += HandleDriftStarted;
        InputManager.Instance.OnDriftEnded += HandleDriftEnded;

        inputSubscribed = true;
    }

    private void UnsubscribeFromInput()
    {
        if (!inputSubscribed)
        {
            return;
        }

        if (InputManager.Instance != null)
        {
            InputManager.Instance.OnSteer -= HandleSteer;
            InputManager.Instance.OnAccelerateStarted -= HandleAccelerateStarted;
            InputManager.Instance.OnAccelerateEnded -= HandleAccelerateEnded;
            InputManager.Instance.OnReverseStarted -= HandleReverseStarted;
            InputManager.Instance.OnReverseEnded -= HandleReverseEnded;
            InputManager.Instance.OnDriftStarted -= HandleDriftStarted;
            InputManager.Instance.OnDriftEnded -= HandleDriftEnded;
        }

        inputSubscribed = false;
    }

    private void HandleSteer(float value)
    {
        steerInput = Mathf.Clamp(value, -1f, 1f);
    }

    private void HandleAccelerateStarted()
    {
        accelerating = true;
    }

    private void HandleAccelerateEnded()
    {
        accelerating = false;
    }

    private void HandleReverseStarted()
    {
        reversing = true;

        if (drifting)
        {
            EndDrift();
        }
    }

    private void HandleReverseEnded()
    {
        reversing = false;
    }

    private void HandleDriftStarted()
    {
        driftButtonHeld = true;

        if (drifting || isHopping || !isGrounded || reversing)
        {
            return;
        }

        hopCoroutine = StartCoroutine(DriftHopRoutine());
    }

    private void HandleDriftEnded()
    {
        driftButtonHeld = false;

        if (drifting)
        {
            EndDrift();
        }
    }

    // ---------------------------------------------------------
    // STATO DEL TERRENO
    // ---------------------------------------------------------

    private void UpdateGroundState()
    {
        if (TryGetGroundHit(out RaycastHit hit))
        {
            float groundAngle = Vector3.Angle(hit.normal, Vector3.up);

            isGrounded = groundAngle <= maximumGroundAngle;
            groundNormal = isGrounded ? hit.normal : Vector3.up;
        }
        else
        {
            isGrounded = false;
            groundNormal = Vector3.up;
        }
    }

    private bool TryGetGroundHit(out RaycastHit nearestHit)
    {
        Vector3 origin =
            groundCheck != null
                ? groundCheck.position
                : transform.position;

        RaycastHit[] hits = Physics.SphereCastAll(
            origin,
            groundCheckRadius,
            Vector3.down,
            groundCheckDistance,
            groundMask,
            QueryTriggerInteraction.Ignore);

        float nearestDistance = float.PositiveInfinity;
        nearestHit = default;
        bool foundGround = false;

        foreach (RaycastHit hit in hits)
        {
            // Ignora il collider del kart e tutti i suoi figli.
            if (hit.collider.transform.IsChildOf(transform))
            {
                continue;
            }

            if (hit.distance < nearestDistance)
            {
                nearestDistance = hit.distance;
                nearestHit = hit;
                foundGround = true;
            }
        }

        return foundGround;
    }

    // ---------------------------------------------------------
    // MOVIMENTO
    // ---------------------------------------------------------

    private void ApplyGravity()
    {
        rb.AddForce(
            Vector3.down * gravity,
            ForceMode.Acceleration);
    }

    private void ApplyAcceleration()
    {
        if (accelerating && reversing)
        {
            ApplyHorizontalDrag();
            return;
        }

        Vector3 movementForward = GetMovementForward();

        if (accelerating)
        {
            rb.AddForce(
                movementForward * acceleration,
                ForceMode.Acceleration);

            return;
        }

        if (reversing)
        {
            rb.AddForce(
                -movementForward * reverseAcceleration,
                ForceMode.Acceleration);

            return;
        }

        ApplyHorizontalDrag();
    }

    private Vector3 GetMovementForward()
    {
        Vector3 movementForward =
            Vector3.ProjectOnPlane(
                transform.forward,
                isGrounded ? groundNormal : Vector3.up);

        if (movementForward.sqrMagnitude < 0.001f)
        {
            return transform.forward;
        }

        return movementForward.normalized;
    }

    private void ApplyHorizontalDrag()
    {
        Vector3 horizontalVelocity =
            Vector3.ProjectOnPlane(
                rb.linearVelocity,
                Vector3.up);

        rb.AddForce(
            -horizontalVelocity * dragWhenNotAccelerating,
            ForceMode.Acceleration);
    }

    private void ApplySteering()
    {
        Vector3 horizontalVelocity =
            Vector3.ProjectOnPlane(
                rb.linearVelocity,
                Vector3.up);

        if (horizontalVelocity.sqrMagnitude < 0.2f)
        {
            return;
        }

        bool movingBackward = IsMovingBackward(horizontalVelocity);

        float referenceSpeed =
            movingBackward
                ? maxReverseSpeed
                : maxSpeed;

        float speedFactor = Mathf.Clamp01(
            horizontalVelocity.magnitude /
            Mathf.Max(referenceSpeed, 0.01f));

        float steeringInputForPhysics;

        if (drifting)
        {
            float alignment =
                Mathf.Clamp(
                    steerInput * driftDirection,
                    -1f,
                    1f);

            float control = Mathf.InverseLerp(
                -1f,
                1f,
                alignment);

            float driftMultiplier = Mathf.Lerp(
                minimumDriftSteeringMultiplier,
                maximumDriftSteeringMultiplier,
                control);

            // La direzione resta sempre quella scelta all'inizio del drift.
            steeringInputForPhysics =
                driftDirection * driftMultiplier;
        }
        else
        {
            steeringInputForPhysics = steerInput;

            if (movingBackward)
            {
                steeringInputForPhysics *= -1f;
            }
        }

        float rotationAmount =
            steeringInputForPhysics *
            steeringSpeed *
            speedFactor *
            Time.fixedDeltaTime;

        Quaternion rotation =
            Quaternion.Euler(
                0f,
                rotationAmount,
                0f);

        rb.MoveRotation(rb.rotation * rotation);
    }

    private void ApplyLateralGrip()
    {
        Vector3 verticalVelocity =
            Vector3.Project(
                rb.linearVelocity,
                Vector3.up);

        Vector3 horizontalVelocity =
            Vector3.ProjectOnPlane(
                rb.linearVelocity,
                Vector3.up);

        Vector3 forwardVelocity =
            transform.forward *
            Vector3.Dot(
                horizontalVelocity,
                transform.forward);

        Vector3 lateralVelocity =
            transform.right *
            Vector3.Dot(
                horizontalVelocity,
                transform.right);

        float grip =
            drifting
                ? driftLateralGrip
                : normalLateralGrip;

        float lateralRetention =
            1f - Mathf.Clamp01(
                grip * Time.fixedDeltaTime);

        lateralVelocity *= lateralRetention;

        rb.linearVelocity =
            forwardVelocity +
            lateralVelocity +
            verticalVelocity;
    }

    private bool IsMovingBackward(Vector3 horizontalVelocity)
    {
        float forwardSpeed =
            Vector3.Dot(
                horizontalVelocity,
                transform.forward);

        return forwardSpeed < -0.1f;
    }

    private float GetForwardSpeed()
    {
        Vector3 horizontalVelocity =
            Vector3.ProjectOnPlane(
                rb.linearVelocity,
                Vector3.up);

        return Vector3.Dot(
            horizontalVelocity,
            transform.forward);
    }

    private void LimitSpeed()
    {
        Vector3 verticalVelocity =
            Vector3.Project(
                rb.linearVelocity,
                Vector3.up);

        Vector3 horizontalVelocity =
            Vector3.ProjectOnPlane(
                rb.linearVelocity,
                Vector3.up);

        float forwardSpeed =
            Vector3.Dot(
                horizontalVelocity,
                transform.forward);

        Vector3 lateralVelocity =
            horizontalVelocity -
            transform.forward * forwardSpeed;

        forwardSpeed = Mathf.Clamp(
            forwardSpeed,
            -maxReverseSpeed,
            maxSpeed);

        rb.linearVelocity =
            transform.forward * forwardSpeed +
            lateralVelocity +
            verticalVelocity;
    }

    // ---------------------------------------------------------
    // DRIFT E HOP
    // ---------------------------------------------------------

    private IEnumerator DriftHopRoutine()
    {
        isHopping = true;

        float elapsed = 0f;
        bool driftChecked = false;

        while (elapsed < hopDuration)
        {
            elapsed += Time.deltaTime;

            float normalizedTime =
                Mathf.Clamp01(
                    elapsed / Mathf.Max(hopDuration, 0.01f));

            // Arco 0 -> 1 -> 0.
            float height =
                Mathf.Sin(normalizedTime * Mathf.PI) *
                hopHeight;

            if (kartModel != null)
            {
                kartModel.localPosition =
                    initialModelPosition +
                    Vector3.up * height;
            }

            if (!driftChecked &&
                normalizedTime >= driftActivationPoint)
            {
                driftChecked = true;
                TryStartDrift();
            }

            yield return null;
        }

        if (kartModel != null)
        {
            kartModel.localPosition = initialModelPosition;
        }

        isHopping = false;
        hopCoroutine = null;
    }

    private void TryStartDrift()
    {
        if (!driftButtonHeld ||
            drifting ||
            reversing ||
            !isGrounded)
        {
            return;
        }

        if (requireAccelerationForDrift && !accelerating)
        {
            return;
        }

        if (Mathf.Abs(steerInput) < minimumDriftSteer)
        {
            return;
        }

        if (GetForwardSpeed() < minimumDriftSpeed)
        {
            return;
        }

        drifting = true;
        driftDirection = steerInput > 0f ? 1 : -1;

        StartDriftParticles();
    }

    private void EndDrift()
    {
        drifting = false;
        driftDirection = 0;

        StopDriftParticles(false);
    }

    // ---------------------------------------------------------
    // VISUALE
    // ---------------------------------------------------------

    private void UpdateVisuals()
    {
        if (kartModel == null)
        {
            return;
        }

        float targetYaw =
            drifting
                ? driftDirection * driftVisualAngle
                : steerInput * steeringVisualAngle;

        currentVisualYaw = Mathf.SmoothDampAngle(
            currentVisualYaw,
            targetYaw,
            ref visualYawVelocity,
            visualSteerSmoothTime);

        smoothedGroundNormal = Vector3.Slerp(
            smoothedGroundNormal,
            isGrounded ? groundNormal : Vector3.up,
            groundAlignmentSpeed * Time.deltaTime);

        Vector3 localGroundNormal =
            transform.InverseTransformDirection(
                smoothedGroundNormal);

        Quaternion groundTilt =
            Quaternion.FromToRotation(
                Vector3.up,
                localGroundNormal);

        Quaternion steeringOffset =
            Quaternion.Euler(
                0f,
                currentVisualYaw,
                0f);

        kartModel.localRotation =
            groundTilt *
            initialModelRotation *
            steeringOffset;
    }

    // ---------------------------------------------------------
    // PARTICELLE
    // ---------------------------------------------------------

    private void StartDriftParticles()
    {
        StopDriftParticles(true);

        List<ParticleSystem> particlesToPlay =
            driftDirection < 0
                ? leftDriftParticles
                : rightDriftParticles;

        PlayParticleList(particlesToPlay);
    }

    private void PlayParticleList(
        List<ParticleSystem> particles)
    {
        foreach (ParticleSystem particle in particles)
        {
            if (particle == null)
            {
                continue;
            }

            particle.Play(true);
        }
    }

    private void StopDriftParticles(bool clearParticles)
    {
        StopParticleList(
            leftDriftParticles,
            clearParticles);

        StopParticleList(
            rightDriftParticles,
            clearParticles);
    }

    private void StopParticleList(
        List<ParticleSystem> particles,
        bool clearParticles)
    {
        ParticleSystemStopBehavior stopBehavior =
            clearParticles
                ? ParticleSystemStopBehavior
                    .StopEmittingAndClear
                : ParticleSystemStopBehavior
                    .StopEmitting;

        foreach (ParticleSystem particle in particles)
        {
            if (particle == null)
            {
                continue;
            }

            particle.Stop(
                true,
                stopBehavior);
        }
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Vector3 origin =
            groundCheck != null
                ? groundCheck.position
                : transform.position;

        Gizmos.DrawWireSphere(
            origin + Vector3.down * groundCheckDistance,
            groundCheckRadius);

        Gizmos.DrawLine(
            origin,
            origin + Vector3.down * groundCheckDistance);
    }
#endif
}