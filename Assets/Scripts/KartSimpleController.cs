using UnityEngine;
using DG.Tweening;

/// <summary>
/// Kart controller essenziale: movimento, sterzo, drift con accumulo di potenza e boost.
/// Nessun VFX, nessuna animazione di ruote/volante.
///
/// SETUP GERARCHIA RICHIESTO (importante):
///
///   Player                     (vuoto, contenitore)
///    ├─ KartVisual              (questo script vive qui; contiene il mesh visivo)
///    │   └─ KartModel           (il mesh 3D vero e proprio, per il lean di sterzo/drift)
///    └─ KartBody                (Rigidbody + Collider, la fisica vera)
///
/// "Sphere" deve essere assegnato a KartBody. KartBody NON deve essere figlio
/// dell'oggetto su cui gira questo script (altrimenti si crea un loop di feedback
/// che fa "impazzire" la posizione: lo script legge la posizione del figlio,
/// sposta il genitore, il che ri-sposta il figlio, ecc.).
/// </summary>
public class KartSimpleController : MonoBehaviour
{
    [Header("Riferimenti")]
    [Tooltip("Il mesh visivo del kart, figlio di questo oggetto. Usato per il lean visivo.")]
    public Transform kartModel;

    [Tooltip("Rigidbody della fisica del kart. NON deve essere figlio di questo Transform.")]
    public Rigidbody sphere;

    [Header("Parametri Movimento")]
    public float acceleration = 30f;
    public float steering = 80f;
    public float gravity = 10f;

    [Header("Parametri Drift")]
    [Tooltip("Quanto si inclina visivamente il modello durante il drift.")]
    public float driftTiltAmount = 15f;

    [Tooltip("Soglie di driftPower per raggiungere i livelli di boost 1/2/3.")]
    public float[] boostThresholds = { 50f, 100f, 150f };

    [Tooltip("Velocità extra (moltiplicatore su currentSpeed) per ciascun livello di boost.")]
    public float[] boostSpeedMultiplier = { 1.3f, 1.6f, 2f };

    [Tooltip("Durata del boost per livello (moltiplicata per il livello raggiunto).")]
    public float boostDurationPerLevel = 0.3f;

    // --- Stato input (arriva da InputManager via eventi) ---
    private float steerInput;
    private bool accelerating;
    private bool inputSubscribed;

    // --- Stato movimento ---
    private float speed, currentSpeed;
    private float rotate, currentRotate;

    // --- Stato drift ---
    public bool drifting { get; private set; }
    private int driftDirection;
    private float driftPower;
    private int driftMode;

    private void OnEnable()
    {
        SubscribeToInput();
    }

    private void Start()
    {
        // Nel caso OnEnable giri prima dell'Awake di InputManager.
        SubscribeToInput();
    }

    private void OnDisable()
    {
        UnsubscribeFromInput();
        steerInput = 0f;
        accelerating = false;
    }

    private void SubscribeToInput()
    {
        if (inputSubscribed || InputManager.Instance == null)
            return;

        InputManager.Instance.OnSteer += HandleSteer;
        InputManager.Instance.OnAccelerateStarted += HandleAccelerateStarted;
        InputManager.Instance.OnAccelerateEnded += HandleAccelerateEnded;
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
            InputManager.Instance.OnDriftStarted -= HandleDriftStarted;
            InputManager.Instance.OnDriftEnded -= HandleDriftEnded;
        }

        inputSubscribed = false;
    }

    private void HandleSteer(float value) => steerInput = Mathf.Clamp(value, -1f, 1f);
    private void HandleAccelerateStarted() => accelerating = true;
    private void HandleAccelerateEnded() => accelerating = false;

    private void HandleDriftStarted()
    {
        if (drifting || Mathf.Approximately(steerInput, 0f))
            return;

        drifting = true;
        driftDirection = steerInput > 0f ? 1 : -1;
    }

    private void HandleDriftEnded()
    {
        if (drifting)
            Boost();
    }

    private void Update()
    {
        if (!inputSubscribed)
            SubscribeToInput();

        // Il visivo segue il corpo fisico.
        transform.position = sphere.transform.position;

        if (accelerating)
            speed = acceleration;

        if (!Mathf.Approximately(steerInput, 0f))
        {
            int dir = steerInput > 0f ? 1 : -1;
            float amount = Mathf.Abs(steerInput);
            Steer(dir, amount);
        }

        if (drifting)
        {
            // Più sterzi nella direzione del drift, più potenza accumuli.
            float powerControl = driftDirection == 1
                ? Remap(steerInput, -1f, 1f, 0.2f, 1f)
                : Remap(steerInput, -1f, 1f, 1f, 0.2f);

            float control = driftDirection == 1
                ? Remap(steerInput, -1f, 1f, 0f, 2f)
                : Remap(steerInput, -1f, 1f, 2f, 0f);

            Steer(driftDirection, control);
            driftPower += powerControl;

            UpdateDriftMode();
        }

        currentSpeed = Mathf.SmoothStep(currentSpeed, speed, Time.deltaTime * 12f);
        speed = 0f;

        currentRotate = Mathf.Lerp(currentRotate, rotate, Time.deltaTime * 4f);
        rotate = 0f;

        // Lean visivo del modello (sterzo normale o inclinazione da drift).
        if (kartModel != null)
        {
            if (!drifting)
            {
                kartModel.localEulerAngles = Vector3.Lerp(
                    kartModel.localEulerAngles,
                    new Vector3(0f, steerInput * driftTiltAmount, kartModel.localEulerAngles.z),
                    0.2f);
            }
            else
            {
                float lean = driftDirection * driftTiltAmount * 1.5f;
                kartModel.localEulerAngles = Vector3.Lerp(
                    kartModel.localEulerAngles,
                    new Vector3(0f, lean, kartModel.localEulerAngles.z),
                    0.2f);
            }
        }
    }

    private void FixedUpdate()
    {
        if (!drifting)
            sphere.AddForce(transform.forward * currentSpeed, ForceMode.Acceleration);
        else
            sphere.AddForce(transform.forward * currentSpeed, ForceMode.Acceleration);

        sphere.AddForce(Vector3.down * gravity, ForceMode.Acceleration);

        transform.eulerAngles = Vector3.Lerp(
            transform.eulerAngles,
            new Vector3(0f, transform.eulerAngles.y + currentRotate, 0f),
            Time.deltaTime * 5f);
    }

    public void Steer(int direction, float amount)
    {
        rotate = steering * direction * amount;
    }

    private void UpdateDriftMode()
    {
        if (boostThresholds.Length < 3)
            return;

        if (driftPower > boostThresholds[2])
            driftMode = 3;
        else if (driftPower > boostThresholds[1])
            driftMode = 2;
        else if (driftPower > boostThresholds[0])
            driftMode = 1;
        else
            driftMode = 0;
    }

    public void Boost()
    {
        drifting = false;

        if (driftMode > 0 && driftMode <= boostSpeedMultiplier.Length)
        {
            float boostedSpeed = currentSpeed * boostSpeedMultiplier[driftMode - 1];
            float duration = boostDurationPerLevel * driftMode;

            DOVirtual.Float(boostedSpeed, currentSpeed, duration, x => currentSpeed = x);
        }

        driftPower = 0f;
        driftMode = 0;
    }

    private static float Remap(float value, float inputMin, float inputMax, float outputMin, float outputMax)
    {
        return (value - inputMin) / (inputMax - inputMin) * (outputMax - outputMin) + outputMin;
    }
}
