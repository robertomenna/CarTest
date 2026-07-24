using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(PlayerInput))]
public class InputManager : MonoBehaviour
{
    public static InputManager Instance { get; private set; }

    // Eventi "puliti" verso il resto del gioco
    public event System.Action<float> OnSteer;
    public event System.Action OnAccelerateStarted;
    public event System.Action OnAccelerateEnded;
    public event System.Action OnDriftStarted;
    public event System.Action OnDriftEnded;
    public event System.Action OnToggleSlowMotion;

    private PlayerInput _playerInput;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        _playerInput = GetComponent<PlayerInput>();
    }

    private void OnEnable()
    {
        // PlayerInput gestisce una sola "map corrente" (quella di Default Map, "Kart").
        // La map "Debug" non fa parte del suo switch automatico, quindi va abilitata
        // esplicitamente qui per farla funzionare in parallelo, sempre attiva.
        _playerInput.actions.FindActionMap("Debug").Enable();
    }

    private void OnDisable()
    {
        _playerInput.actions.FindActionMap("Debug").Disable();
    }

    // Questo metodo viene chiamato automaticamente da PlayerInput
    // quando il suo campo "Behavior" e' impostato su "Invoke C# Events".
    // Il nome "OnActionTriggered" e' una convenzione richiesta da Unity: non cambiarlo.
    public void OnActionTriggered(InputAction.CallbackContext ctx)
    {
        switch (ctx.action.name)
        {
            case "Steer":
                if (ctx.performed) OnSteer?.Invoke(ctx.ReadValue<float>());
                else if (ctx.canceled) OnSteer?.Invoke(0f);
                break;

            case "Accelerate":
                if (ctx.started) OnAccelerateStarted?.Invoke();
                else if (ctx.canceled) OnAccelerateEnded?.Invoke();
                break;

            case "Drift":
                if (ctx.started) OnDriftStarted?.Invoke();
                else if (ctx.canceled) OnDriftEnded?.Invoke();
                break;

            case "ToggleSlowMotion":
                if (ctx.started) OnToggleSlowMotion?.Invoke();
                break;
        }
    }
}