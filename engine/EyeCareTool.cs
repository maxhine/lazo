using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Lazo
{
    /// <summary>
    /// Descanso Visual del Lazo clásico para la isla: el mismo EyeCareService (temporizadores, estadísticas del día, archivo
    /// eyecare.txt y las ventanas de aviso, compilados tal cual desde ../lazo/src) en su propio hilo con Dispatcher, porque
    /// el servicio usa DispatcherTimer y ventanas WPF. La isla dibuja la tarjeta (relojes, cuentas, ajustes).
    /// </summary>
    internal sealed partial class EngineHost
    {
        private Dispatcher _eyeDispatcher;
        private readonly object _eyeLock = new object();

        /// <summary>Arranca el servicio una vez (los avisos tienen que salir aunque la isla esté cerrada).</summary>
        private Dispatcher EyeDispatcher()
        {
            lock (_eyeLock)
            {
                if (_eyeDispatcher != null) return _eyeDispatcher;
                ManualResetEvent ready = new ManualResetEvent(false);
                Thread thread = new Thread(() =>
                {
                    Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
                    // Los avisos usan los colores del tema del clásico (acento, paleta): hay que cargarlo antes de crear una ventana.
                    Theme.Load();
                    EyeCareService service = EyeCareService.Instance;
                    service.Ticked += () => { PushEyeCare(); };
                    service.StateChanged += () => { PushEyeCare(); };
                    service.Start();
                    _eyeDispatcher = dispatcher;
                    ready.Set();
                    Dispatcher.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Name = "Lazo eye care";
                thread.Start();
                ready.WaitOne();
                return _eyeDispatcher;
            }
        }

        private static Dictionary<string, object> EyeCareState()
        {
            EyeCareService s = EyeCareService.Instance;
            return new Dictionary<string, object>
            {
                { "type", "eyecare.state" },
                { "microLeft", s.SecondsToMicroBreak }, { "activeLeft", s.SecondsToActivePause },
                { "workToday", s.TotalWorkSecondsToday }, { "micro", s.MicroBreaksCompleted }, { "active", s.ActivePausesCompleted },
                { "paused", s.IsPaused }, { "sound", s.SoundEnabled },
                { "clock", s.ClockMode == EyeCareClockMode.Analog ? "analog" : "digital" },
                { "rest", s.RestStyle == EyeCareRestStyle.Eyes ? "eyes" : "notice" },
                { "microEvery", EyeCareService.MicroBreakInterval }, { "activeEvery", EyeCareService.ActivePauseInterval },
                { "microFor", EyeCareService.MicroBreakDuration }, { "activeFor", EyeCareService.ActivePauseDuration }
            };
        }

        private void PushEyeCare()
        {
            Broadcast(EyeCareState());
        }

        private async Task EyeCareCommand(Client client, string cmd, Dictionary<string, object> m)
        {
            Dispatcher dispatcher = EyeDispatcher();
            string value = Str(m, "value") ?? "";
            await dispatcher.InvokeAsync(() =>
            {
                EyeCareService s = EyeCareService.Instance;
                switch (cmd)
                {
                    case "eyecare.pause": s.SetPaused(Flag(m, "value")); break;
                    case "eyecare.sound": s.SetSoundEnabled(Flag(m, "value")); break;
                    case "eyecare.clock": s.SetClockMode(value == "analog" ? EyeCareClockMode.Analog : EyeCareClockMode.Digital); break;
                    case "eyecare.rest": s.SetRestStyle(value == "eyes" ? EyeCareRestStyle.Eyes : EyeCareRestStyle.Notice); break;
                    case "eyecare.trigger":
                        if (value == "active") s.TriggerActivePause();
                        else s.TriggerMicroBreak();
                        break;
                    case "eyecare.state": break;
                }
            }).Task;
            await Send(client, EyeCareState());
        }
    }
}
