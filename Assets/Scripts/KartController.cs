using System.Collections.Generic;
using UnityEngine;
using DG.Tweening;
using Unity.Cinemachine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public class KartController : MonoBehaviour
{
    private Volume volume;
    private VolumeProfile profile;
    private ChromaticAberration chromatic;

    public Transform kartModel;
    public Transform kartNormal;
    public Rigidbody sphere;

    public List<ParticleSystem> primaryParticles = new List<ParticleSystem>();
    public List<ParticleSystem> secondaryParticles = new List<ParticleSystem>();

    float speed, currentSpeed;
    float rotate, currentRotate;
    int driftDirection;
    float driftPower;
    int driftMode = 0;
    bool first, second, third;
    Color c;

    // Stato ricevuto dal nuovo Input System.
    private float steerInput;
    private bool accelerating;
    private bool inputSubscribed;

    [Header("Bools")]
    public bool drifting;

    [Header("Parameters")]
    public float acceleration = 30f;
    public float steering = 80f;
    public float gravity = 10f;
    public LayerMask layerMask;

    [Header("Model Parts")]
    public Transform frontWheels;
    public Transform backWheels;
    public Transform steeringWheel;

    [Header("Particles")]
    public Transform wheelParticles;
    public Transform flashParticles;
    public Color[] turboColors;

    [Header("Cinemachine")]
    [SerializeField] private CinemachineImpulseSource impulseSource;

    [Header("URP Volume")]
    [SerializeField] private Volume postProcessingVolume;

    private void OnEnable()
    {
        SubscribeToInput();
    }

    private void Start()
    {
        // OnEnable potrebbe essere eseguito prima dell'Awake di InputManager.
        // In Start tutti gli Awake della scena sono già stati eseguiti.
        SubscribeToInput();

        volume = postProcessingVolume;

        if (volume == null && Camera.main != null)
        {
            volume = Camera.main.GetComponent<Volume>();
        }

        if (volume != null)
        {
            profile = volume.profile;
            profile.TryGet(out chromatic);
        }
        else
        {
            Debug.LogWarning(
                "KartController: nessun Volume URP assegnato. " +
                "La Chromatic Aberration del boost non verrà utilizzata.",
                this);
        }

        if (wheelParticles != null)
        {
            if (wheelParticles.childCount > 0)
            {
                for (int i = 0; i < wheelParticles.GetChild(0).childCount; i++)
                {
                    ParticleSystem particle = wheelParticles
                        .GetChild(0)
                        .GetChild(i)
                        .GetComponent<ParticleSystem>();

                    if (particle != null)
                    {
                        primaryParticles.Add(particle);
                    }
                }
            }

            if (wheelParticles.childCount > 1)
            {
                for (int i = 0; i < wheelParticles.GetChild(1).childCount; i++)
                {
                    ParticleSystem particle = wheelParticles
                        .GetChild(1)
                        .GetChild(i)
                        .GetComponent<ParticleSystem>();

                    if (particle != null)
                    {
                        primaryParticles.Add(particle);
                    }
                }
            }
        }

        if (flashParticles != null)
        {
            foreach (
                ParticleSystem particle in
                flashParticles.GetComponentsInChildren<ParticleSystem>())
            {
                secondaryParticles.Add(particle);
            }
        }
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
        {
            return;
        }

        InputManager.Instance.OnSteer += HandleSteer;
        InputManager.Instance.OnAccelerateStarted += HandleAccelerateStarted;
        InputManager.Instance.OnAccelerateEnded += HandleAccelerateEnded;
        InputManager.Instance.OnDriftStarted += HandleDriftStarted;
        InputManager.Instance.OnDriftEnded += HandleDriftEnded;
        InputManager.Instance.OnToggleSlowMotion += HandleToggleSlowMotion;

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
            InputManager.Instance.OnDriftStarted -= HandleDriftStarted;
            InputManager.Instance.OnDriftEnded -= HandleDriftEnded;
            InputManager.Instance.OnToggleSlowMotion -= HandleToggleSlowMotion;
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

    private void HandleDriftStarted()
    {
        if (drifting || Mathf.Approximately(steerInput, 0f))
        {
            return;
        }

        drifting = true;
        driftDirection = steerInput > 0f ? 1 : -1;

        foreach (ParticleSystem p in primaryParticles)
        {
            var main = p.main;
            main.startColor = Color.clear;
            p.Play();
        }

        kartModel.parent.DOComplete();
        kartModel.parent.DOPunchPosition(
            transform.up * 0.2f,
            0.3f,
            5,
            1f);
    }

    private void HandleDriftEnded()
    {
        if (drifting)
        {
            Boost();
        }
    }

    private void HandleToggleSlowMotion()
    {
        Time.timeScale =
            Mathf.Approximately(Time.timeScale, 1f)
                ? 0.2f
                : 1f;
    }

    private void Update()
    {
        // Nel caso in cui InputManager sia stato attivato dopo il kart.
        if (!inputSubscribed)
        {
            SubscribeToInput();
        }

        // Follow Collider
        transform.position =
            sphere.transform.position - new Vector3(0f, 0.4f, 0f);

        // Accelerate
        if (accelerating)
        {
            speed = acceleration;
        }

        // Steer
        if (!Mathf.Approximately(steerInput, 0f))
        {
            int dir = steerInput > 0f ? 1 : -1;
            float amount = Mathf.Abs(steerInput);

            Steer(dir, amount);
        }

        // Drift
        if (drifting)
        {
            float control =
                driftDirection == 1
                    ? Remap(steerInput, -1f, 1f, 0f, 2f)
                    : Remap(steerInput, -1f, 1f, 2f, 0f);

            float powerControl =
                driftDirection == 1
                    ? Remap(steerInput, -1f, 1f, 0.2f, 1f)
                    : Remap(steerInput, -1f, 1f, 1f, 0.2f);

            Steer(driftDirection, control);
            driftPower += powerControl;

            ColorDrift();
        }

        currentSpeed = Mathf.SmoothStep(
            currentSpeed,
            speed,
            Time.deltaTime * 12f);

        speed = 0f;

        currentRotate = Mathf.Lerp(
            currentRotate,
            rotate,
            Time.deltaTime * 4f);

        rotate = 0f;

        // Animations

        // a) Kart
        if (!drifting)
        {
            kartModel.localEulerAngles = Vector3.Lerp(
                kartModel.localEulerAngles,
                new Vector3(
                    0f,
                    90f + steerInput * 15f,
                    kartModel.localEulerAngles.z),
                0.2f);
        }
        else
        {
            float control =
                driftDirection == 1
                    ? Remap(steerInput, -1f, 1f, 0.5f, 2f)
                    : Remap(steerInput, -1f, 1f, 2f, 0.5f);

            kartModel.parent.localRotation = Quaternion.Euler(
                0f,
                Mathf.LerpAngle(
                    kartModel.parent.localEulerAngles.y,
                    control * 15f * driftDirection,
                    0.2f),
                0f);
        }

        // b) Wheels
        frontWheels.localEulerAngles = new Vector3(
            0f,
            steerInput * 15f,
            frontWheels.localEulerAngles.z);

        frontWheels.localEulerAngles += new Vector3(
            0f,
            0f,
            sphere.linearVelocity.magnitude / 2f);

        backWheels.localEulerAngles += new Vector3(
            0f,
            0f,
            sphere.linearVelocity.magnitude / 2f);

        // c) Steering Wheel
        steeringWheel.localEulerAngles = new Vector3(
            -25f,
            90f,
            steerInput * 45f);
    }

    private void FixedUpdate()
    {
        // Forward Acceleration
        if (!drifting)
        {
            sphere.AddForce(
                -kartModel.transform.right * currentSpeed,
                ForceMode.Acceleration);
        }
        else
        {
            sphere.AddForce(
                transform.forward * currentSpeed,
                ForceMode.Acceleration);
        }

        // Gravity
        sphere.AddForce(
            Vector3.down * gravity,
            ForceMode.Acceleration);

        // Steering
        transform.eulerAngles = Vector3.Lerp(
            transform.eulerAngles,
            new Vector3(
                0f,
                transform.eulerAngles.y + currentRotate,
                0f),
            Time.deltaTime * 5f);

        RaycastHit hitOn;
        RaycastHit hitNear;

        Physics.Raycast(
            transform.position + transform.up * 0.1f,
            Vector3.down,
            out hitOn,
            1.1f,
            layerMask);

        bool hitGround = Physics.Raycast(
            transform.position + transform.up * 0.1f,
            Vector3.down,
            out hitNear,
            2f,
            layerMask);

        // Normal Rotation
        if (hitGround)
        {
            kartNormal.up = Vector3.Lerp(
                kartNormal.up,
                hitNear.normal,
                Time.deltaTime * 8f);

            kartNormal.Rotate(
                0f,
                transform.eulerAngles.y,
                0f);
        }
    }

    public void Boost()
    {
        drifting = false;

        if (driftMode > 0)
        {
            DOVirtual.Float(
                currentSpeed * 3f,
                currentSpeed,
                0.3f * driftMode,
                Speed);

            if (chromatic != null)
            {
                DOVirtual
                    .Float(0f, 1f, 0.5f, ChromaticAmount)
                    .OnComplete(() =>
                        DOVirtual.Float(
                            1f,
                            0f,
                            0.5f,
                            ChromaticAmount));
            }

            Transform tube001 = kartModel.Find("Tube001");
            Transform tube002 = kartModel.Find("Tube002");

            if (tube001 != null)
            {
                ParticleSystem particle =
                    tube001.GetComponentInChildren<ParticleSystem>();

                if (particle != null)
                {
                    particle.Play();
                }
            }

            if (tube002 != null)
            {
                ParticleSystem particle =
                    tube002.GetComponentInChildren<ParticleSystem>();

                if (particle != null)
                {
                    particle.Play();
                }
            }
        }

        driftPower = 0f;
        driftMode = 0;

        first = false;
        second = false;
        third = false;

        foreach (ParticleSystem p in primaryParticles)
        {
            var main = p.main;
            main.startColor = Color.clear;
            p.Stop();
        }

        kartModel.parent
            .DOLocalRotate(Vector3.zero, 0.5f)
            .SetEase(Ease.OutBack);
    }

    public void Steer(int direction, float amount)
    {
        rotate = steering * direction * amount;
    }

    public void ColorDrift()
    {
        if (!first)
        {
            c = Color.clear;
        }

        if (driftPower > 50f &&
            driftPower < 99f &&
            !first)
        {
            first = true;
            c = turboColors[0];
            driftMode = 1;

            PlayFlashParticle(c);
        }

        if (driftPower > 100f &&
            driftPower < 149f &&
            !second)
        {
            second = true;
            c = turboColors[1];
            driftMode = 2;

            PlayFlashParticle(c);
        }

        if (driftPower > 150f && !third)
        {
            third = true;
            c = turboColors[2];
            driftMode = 3;

            PlayFlashParticle(c);
        }

        foreach (ParticleSystem p in primaryParticles)
        {
            var pmain = p.main;
            pmain.startColor = c;
        }

        foreach (ParticleSystem p in secondaryParticles)
        {
            var pmain = p.main;
            pmain.startColor = c;
        }
    }

    private void PlayFlashParticle(Color color)
    {
        if (impulseSource != null)
        {
            impulseSource.GenerateImpulse();
        }

        foreach (ParticleSystem p in secondaryParticles)
        {
            var pmain = p.main;
            pmain.startColor = color;
            p.Play();
        }
    }

    private void Speed(float x)
    {
        currentSpeed = x;
    }

    private void ChromaticAmount(float x)
    {
        if (chromatic != null)
        {
            chromatic.intensity.value = x;
        }
    }

    private static float Remap(
        float value,
        float inputMin,
        float inputMax,
        float outputMin,
        float outputMax)
    {
        return (value - inputMin) /
               (inputMax - inputMin) *
               (outputMax - outputMin) +
               outputMin;
    }

    private void OnDrawGizmos()
    {
        Gizmos.color = Color.red;

        Gizmos.DrawLine(
            transform.position + transform.up,
            transform.position - transform.up * 2f);
    }
}