using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Controller hover standalone: nessuna interfaccia, nessuna classe base,
/// comunica con InputManager tramite eventi (stesso pattern di KartSimpleController).
///
/// Fisica di hover, movimento, rotazione normale, grip e tilt visivo sono
/// portate identiche da NewHoverVehicleController. L'unica parte sostituita
/// è il DRIFT: invece dell'handbrake che cambia direzione ogni frame in base
/// al segno di "steer", qui la direzione del drift viene decisa una sola
/// volta all'inizio (come nel kart) e la curva si controlla interpolando
/// tra un moltiplicatore minimo e massimo in base a quanto assecondi o
/// contrasti quella direzione con lo sterzo.
/// </summary>
[RequireComponent(typeof(Rigidbody))]
public class SimpleHoverVehicleController : MonoBehaviour
{
    #region HOVER (LEVITAZIONE)

    [Header("Hover - Rilevamento Terreno")]
    [Tooltip("Punto da cui parte il raycast verso il basso. Se vuoto viene usato transform.position.")]
    [SerializeField] private Transform hoverOrigin;

    [Tooltip("Layer considerati terreno valido per l'hover.")]
    [SerializeField] private LayerMask groundLayer;

    [Tooltip("Altezza di hover desiderata dal terreno.")]
    [SerializeField] private float hoverHeight = 1.2f;

    [Tooltip("Distanza massima del raycast: oltre questa il veicolo cade.")]
    [SerializeField] private float maxHoverDistance = 2.5f;

    [Header("Hover - Molla")]
    [SerializeField] private float springStrength = 80f;
    [SerializeField] private float damperStrength = 12f;
    [SerializeField] private float groundAlignStrength = 6f;
    [SerializeField] private float fallAcceleration = 12f;

    private float lastSpringLength;
    private float currentDriftYawVelocity; // per SmoothDampAngle durante la derapata

    #endregion

    #region MOVIMENTO E GRIP

    [Header("Movimento")]
    [SerializeField] private float driveForce = 40f;

    [Header("Grip (aderenza laterale)")]
    [Tooltip("Frazione di velocità laterale annullata ad ogni step fisico (0 = scivola come un hovercraft, 1 = non scivola mai lateralmente).")]
    [Range(0f, 1f)]
    [SerializeField] private float grip = 0.85f;

    [Tooltip("Moltiplicatore della grip durante la derapata: più è basso, più la macchina scivola lateralmente.")]
    [Range(0f, 1f)]
    [SerializeField] private float driftGripMultiplier = 0.15f;

    #endregion

    #region ROTAZIONE NORMALE

    [Header("Rotazione (fuori dal drift)")]
    [SerializeField] private float baseTurnTorque = 25f;

    [Range(0.05f, 1f)]
    [SerializeField] private float minTurnFactor = 0.35f;

    [SerializeField] private float turnSpeedReference = 20f;

    #endregion

    #region DRIFT

    [Header("Drift - Innesco")]
    [Tooltip("Se vero, il drift può iniziare solo mentre si accelera (come nel kart).")]
    [SerializeField] private bool requireAccelerationForDrift = true;

    [Tooltip("Sterzo minimo (valore assoluto) richiesto per innescare il drift.")]
    [Range(0f, 1f)]
    [SerializeField] private float driftMinSteer = 0.2f;

    [Tooltip("Velocità minima richiesta per iniziare il drift.")]
    [SerializeField] private float driftMinSpeed = 6f;

    [Tooltip("Velocità sotto la quale il drift finisce automaticamente.")]
    [SerializeField] private float driftExitSpeed = 3f;

    [Header("Drift - Curva")]
    [Tooltip("Velocità di rotazione base (gradi/secondo) durante il drift, prima di applicare il moltiplicatore.")]
    [SerializeField] private float driftTurnRateDegPerSec = 90f;

    [Tooltip("Moltiplicatore minimo: quando contro-sterzi rispetto alla direzione del drift.")]
    [Range(0f, 1f)]
    [SerializeField] private float minimumDriftSteeringMultiplier = 0.4f;

    [Tooltip("Moltiplicatore massimo: quando assecondi la direzione del drift.")]
    [SerializeField] private float maximumDriftSteeringMultiplier = 1.15f;

    [Tooltip("Tempo per raggiungere la velocità di rotazione target, invece di uno scatto secco.")]
    [SerializeField] private float driftTurnSmoothing = 0.15f;

    [Header("Drift - Hop")]
    [Tooltip("Se vero, prima del drift c'è un piccolo salto visivo (come nel kart) e la direzione viene bloccata al momento giusto dell'arco.")]
    [SerializeField] private bool useHopBeforeDrift = true;

    [SerializeField] private float hopHeight = 0.15f;
    [SerializeField] private float hopDuration = 0.22f;

    [Range(0.1f, 0.9f)]
    [SerializeField] private float driftActivationPoint = 0.45f;

    #endregion

    #region VFX DRIFT

    [Header("VFX Drift")]
    [SerializeField] private List<ParticleSystem> leftDriftParticles = new();
    [SerializeField] private List<ParticleSystem> rightDriftParticles = new();

    #endregion

    #region FEEDBACK VISIVO

    [Header("Feedback Visivo")]
    [Tooltip("Transform figlio col modello grafico. Inclinato per il tilt, mai il Rigidbody.")]
    [SerializeField] private Transform visualRoot;

    [SerializeField] private float maxRollAngle = 12f;
    [SerializeField] private float maxPitchAngle = 6f;
    [SerializeField] private float tiltSmoothing = 8f;

    private float currentVisualRoll;
    private float currentVisualPitch;
    private Vector3 visualRootInitialLocalPosition;
    private Quaternion visualRootInitialLocalRotation;

    #endregion

    #region MASSA

    [Header("Massa")]
    [SerializeField] private Vector3 centerOfMassOffset = new Vector3(0f, -0.5f, 0f);
    [SerializeField] private float linearDamping = 1f;
    [SerializeField] private float angularDamping = 3f;

    #endregion

    #region STATO RUNTIME

    private Rigidbody rb;

    // Input, impostati dagli eventi di InputManager.
    private float steer;
    private bool accelerating;
    private bool reversing;
    private bool driftButtonHeld;
    private bool inputSubscribed;

    private bool isFalling;
    private bool drifting;
    private int driftDirection;
    private bool isHopping;
    private Coroutine hopCoroutine;

    #endregion

    #region PROPRIETÀ PUBBLICHE (debug/HUD)

    public float CurrentSpeed => rb.linearVelocity.magnitude;
    public bool IsFalling => isFalling;
    public bool IsDrifting => drifting;

    #endregion

    #region UNITY METHODS

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        rb.useGravity = false;
        rb.centerOfMass = centerOfMassOffset;
        rb.linearDamping = linearDamping;
        rb.angularDamping = angularDamping;

        if (hoverOrigin == null)
            hoverOrigin = transform;

        if (visualRoot != null)
        {
            visualRootInitialLocalPosition = visualRoot.localPosition;
            visualRootInitialLocalRotation = visualRoot.localRotation;
        }
    }

    private void OnEnable()
    {
        SubscribeToInput();
    }

    private void Start()
    {
        // Nel caso OnEnable giri prima dell'Awake di InputManager.
        SubscribeToInput();
        StopDriftParticles(true);
    }

    private void OnDisable()
    {
        UnsubscribeFromInput();

        steer = 0f;
        accelerating = false;
        reversing = false;
        driftButtonHeld = false;
        drifting = false;
        driftDirection = 0;

        if (hopCoroutine != null)
        {
            StopCoroutine(hopCoroutine);
            hopCoroutine = null;
        }

        isHopping = false;

        if (visualRoot != null)
        {
            visualRoot.localPosition = visualRootInitialLocalPosition;
            visualRoot.localRotation = visualRootInitialLocalRotation;
        }

        StopDriftParticles(true);
    }

    private void Update()
    {
        if (!inputSubscribed)
            SubscribeToInput();

        HandleVisualFeedback();
    }

    private void FixedUpdate()
    {
        HandleHover();

        if (drifting && ShouldEndDrift())
        {
            EndDrift();
        }

        if (!isFalling)
        {
            HandleGrip();
            HandleMovement();
            HandleRotation();
        }
        else
        {
            SimulateFall();
        }
    }

    #endregion

    #region INPUT (INPUTMANAGER)

    private void SubscribeToInput()
    {
        if (inputSubscribed || InputManager.Instance == null)
            return;

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
            return;

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

    private void HandleSteer(float value) => steer = Mathf.Clamp(value, -1f, 1f);
    private void HandleAccelerateStarted() => accelerating = true;
    private void HandleAccelerateEnded() => accelerating = false;
    private void HandleReverseStarted() => reversing = true;
    private void HandleReverseEnded() => reversing = false;

    private void HandleDriftStarted()
    {
        driftButtonHeld = true;

        if (drifting || isHopping || isFalling)
            return;

        if (useHopBeforeDrift)
        {
            hopCoroutine = StartCoroutine(DriftHopRoutine());
        }
        else
        {
            TryStartDrift();
        }
    }

    private void HandleDriftEnded()
    {
        driftButtonHeld = false;

        if (drifting)
            EndDrift();
    }

    #endregion

    #region HOVER

    private void HandleHover()
    {
        Vector3 origin = hoverOrigin.position;

        if (Physics.Raycast(origin, -transform.up, out RaycastHit hit, maxHoverDistance, groundLayer))
        {
            isFalling = false;

            float distanceToGround = hit.distance;
            float springLength = hoverHeight - distanceToGround;

            float springVelocity = (springLength - lastSpringLength) / Time.fixedDeltaTime;
            lastSpringLength = springLength;

            float springForce = springStrength * springLength;
            float damperForce = damperStrength * springVelocity;

            Debug.Log($"[Hover] HIT {hit.collider.name} | dist={distanceToGround:F2} | springLength={springLength:F2} | forza={springForce + damperForce:F2}");

            rb.AddForceAtPosition(transform.up * (springForce + damperForce), origin);

            Vector3 alignAxis = Vector3.Cross(transform.up, hit.normal);
            float alignAngle = Vector3.Angle(transform.up, hit.normal);
            rb.AddTorque(alignAxis * (alignAngle * groundAlignStrength));
        }
        else
        {
            Debug.Log($"[Hover] NESSUN HIT da {origin} verso {-transform.up} (maxDistance={maxHoverDistance}, layer={groundLayer.value})");
            isFalling = true;
        }
    }

    private void SimulateFall()
    {
        rb.AddForce(Vector3.down * fallAcceleration, ForceMode.Acceleration);
    }

    #endregion

    #region GRIP

    private void HandleGrip()
    {
        float currentGrip = drifting ? grip * driftGripMultiplier : grip;

        Vector3 lateralVelocity = transform.right * Vector3.Dot(rb.linearVelocity, transform.right);
        rb.linearVelocity -= lateralVelocity * currentGrip;
    }

    #endregion

    #region MOVIMENTO

    private void HandleMovement()
    {
        float drive = (accelerating ? 1f : 0f) - (reversing ? 1f : 0f);
        Vector3 forwardForce = transform.forward * (drive * driveForce);
        rb.AddForce(forwardForce, ForceMode.Acceleration);
    }

    private float GetForwardSpeed()
    {
        return Vector3.Dot(rb.linearVelocity, transform.forward);
    }

    #endregion

    #region ROTAZIONE

    private void HandleRotation()
    {
        if (drifting)
        {
            // Direzione bloccata all'inizio del drift. La curva si stringe o
            // si allarga in base a quanto lo sterzo asseconda o contrasta
            // quella direzione, interpolando tra i due moltiplicatori.
            float alignment = Mathf.Clamp(steer * driftDirection, -1f, 1f);
            float control = Mathf.InverseLerp(-1f, 1f, alignment);

            float driftMultiplier = Mathf.Lerp(
                minimumDriftSteeringMultiplier,
                maximumDriftSteeringMultiplier,
                control);

            float targetYawSpeed = driftDirection * driftTurnRateDegPerSec * driftMultiplier * Mathf.Deg2Rad;

            Vector3 angularVelocity = rb.angularVelocity;
            angularVelocity.y = Mathf.SmoothDampAngle(
                angularVelocity.y * Mathf.Rad2Deg,
                targetYawSpeed * Mathf.Rad2Deg,
                ref currentDriftYawVelocity,
                driftTurnSmoothing) * Mathf.Deg2Rad;

            rb.angularVelocity = angularVelocity;
        }
        else
        {
            float speedFactor = Mathf.Clamp01(rb.linearVelocity.magnitude / turnSpeedReference);
            float turnMultiplier = Mathf.Lerp(1f, minTurnFactor, speedFactor);
            float yawTorque = steer * baseTurnTorque * turnMultiplier;

            rb.AddRelativeTorque(Vector3.up * yawTorque);
        }
    }

    #endregion

    #region DRIFT - HOP E STATO

    private IEnumerator DriftHopRoutine()
    {
        isHopping = true;

        float elapsed = 0f;
        bool driftChecked = false;

        while (elapsed < hopDuration)
        {
            elapsed += Time.deltaTime;

            float normalizedTime = Mathf.Clamp01(elapsed / Mathf.Max(hopDuration, 0.01f));

            // Arco 0 -> 1 -> 0, applicato solo al visivo.
            if (visualRoot != null)
            {
                float height = Mathf.Sin(normalizedTime * Mathf.PI) * hopHeight;
                visualRoot.localPosition = visualRootInitialLocalPosition + Vector3.up * height;
            }

            if (!driftChecked && normalizedTime >= driftActivationPoint)
            {
                driftChecked = true;
                TryStartDrift();
            }

            yield return null;
        }

        if (visualRoot != null)
            visualRoot.localPosition = visualRootInitialLocalPosition;

        isHopping = false;
        hopCoroutine = null;
    }

    private void TryStartDrift()
    {
        if (!driftButtonHeld || drifting || isFalling)
            return;

        if (requireAccelerationForDrift && !accelerating)
            return;

        if (Mathf.Abs(steer) < driftMinSteer)
            return;

        if (GetForwardSpeed() < driftMinSpeed)
            return;

        drifting = true;
        driftDirection = steer > 0f ? 1 : -1;

        StartDriftParticles();
    }

    private bool ShouldEndDrift()
    {
        if (isFalling)
            return true;

        if (GetForwardSpeed() < driftExitSpeed)
            return true;

        return false;
    }

    private void EndDrift()
    {
        drifting = false;
        driftDirection = 0;

        StopDriftParticles(false);
    }

    #endregion

    #region VFX DRIFT

    private void StartDriftParticles()
    {
        StopDriftParticles(true);

        List<ParticleSystem> particlesToPlay = driftDirection < 0
            ? leftDriftParticles
            : rightDriftParticles;

        PlayParticleList(particlesToPlay);
    }

    private void PlayParticleList(List<ParticleSystem> particles)
    {
        foreach (ParticleSystem particle in particles)
        {
            if (particle == null) continue;
            particle.Play(true);
        }
    }

    private void StopDriftParticles(bool clearParticles)
    {
        StopParticleList(leftDriftParticles, clearParticles);
        StopParticleList(rightDriftParticles, clearParticles);
    }

    private void StopParticleList(List<ParticleSystem> particles, bool clearParticles)
    {
        ParticleSystemStopBehavior stopBehavior = clearParticles
            ? ParticleSystemStopBehavior.StopEmittingAndClear
            : ParticleSystemStopBehavior.StopEmitting;

        foreach (ParticleSystem particle in particles)
        {
            if (particle == null) continue;
            particle.Stop(true, stopBehavior);
        }
    }

    #endregion

    #region FEEDBACK VISIVO

    private void HandleVisualFeedback()
    {
        if (visualRoot == null) return;

        float speedFactor = rb != null ? Mathf.Clamp01(rb.linearVelocity.magnitude / turnSpeedReference) : 0f;

        float targetRoll = -steer * maxRollAngle * speedFactor;
        float targetPitch = ((reversing ? 1f : 0f) - (accelerating ? 1f : 0f)) * maxPitchAngle;

        currentVisualRoll = Mathf.LerpAngle(currentVisualRoll, targetRoll, Time.deltaTime * tiltSmoothing);
        currentVisualPitch = Mathf.LerpAngle(currentVisualPitch, targetPitch, Time.deltaTime * tiltSmoothing);

        Quaternion tilt = Quaternion.Euler(currentVisualPitch, 0f, currentVisualRoll);

        // Il tilt si somma alla rotazione iniziale del modello (che può
        // contenere una correzione d'importazione, es. -90 su X), invece
        // di sovrascriverla del tutto.
        visualRoot.localRotation = visualRootInitialLocalRotation * tilt;
    }

    #endregion

#if UNITY_EDITOR
    #region GIZMO

    private void OnDrawGizmos()
    {
        Transform origin = hoverOrigin != null ? hoverOrigin : transform;

        // Direzione reale usata dal raycast: -transform.up, non Vector3.down.
        // Se il veicolo è storto, questa linea lo mostra chiaramente.
        Vector3 rayDirection = -transform.up;
        Vector3 rayEnd = origin.position + rayDirection * maxHoverDistance;

        bool hitSomething = Physics.Raycast(origin.position, rayDirection, out RaycastHit hit, maxHoverDistance, groundLayer);

        Gizmos.color = hitSomething ? Color.green : Color.red;
        Gizmos.DrawLine(origin.position, rayEnd);
        Gizmos.DrawWireSphere(origin.position, 0.08f);

        if (hitSomething)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawWireSphere(hit.point, 0.12f);
            Gizmos.DrawLine(hit.point, hit.point + hit.normal * 0.5f);
        }

        // Altezza target di hover, per confronto visivo.
        Gizmos.color = Color.cyan;
        Vector3 targetPoint = origin.position - transform.up * hoverHeight;
        Gizmos.DrawWireSphere(targetPoint, 0.06f);
    }

    #endregion
#endif
}