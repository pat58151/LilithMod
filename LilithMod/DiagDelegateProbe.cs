using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.SceneManagement;

// URP's per-frame callback signature, as Il2CppInterop generates it.
using FrameCallback = Il2CppSystem.Action<
    UnityEngine.Rendering.ScriptableRenderContext,
    Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<UnityEngine.Camera>>;

namespace LilithMod
{
    /// <summary>
    /// Temporary diagnostic. Injected MonoBehaviour Update() never dispatches on this game
    /// build because Il2CppInterop cannot resolve Class::Init and substitutes a no-op, so the
    /// class method cache Unity needs for per-frame callbacks is never built. Awake() still
    /// works because AddComponent invokes it directly.
    ///
    /// Delegates take a different path (DelegateSupport, which did initialise). This probes
    /// which - if any - Unity-to-managed delegate callbacks still fire, to find a tick source.
    /// </summary>
    internal static class DiagDelegateProbe
    {
        private static bool _preRender, _sceneLoaded, _beginFrame, _frameTick, _inputTick;

        public static void Install()
        {
            Try("Camera.onPreRender", () =>
            {
                Camera.onPreRender += DelegateSupport.ConvertDelegate<Camera.CameraCallback>(
                    new Action<Camera>(_ => Once(ref _preRender, "Camera.onPreRender")));
            });

            Try("Camera.onPostRender", () =>
            {
                Camera.onPostRender += DelegateSupport.ConvertDelegate<Camera.CameraCallback>(
                    new Action<Camera>(_ => Once(ref _beginFrame, "Camera.onPostRender")));
            });

            // This game renders through URP, so the legacy Camera callbacks above never fire.
            // RenderPipelineManager is the SRP equivalent and runs every frame.
            Try("RenderPipelineManager.beginFrameRendering", () =>
            {
                // Il2CppInterop exposes this as a plain static delegate field rather than an
                // event, so there is no add_ accessor and += does not apply. Combine by hand.
                var cb = DelegateSupport.ConvertDelegate<FrameCallback>(
                    new Action<UnityEngine.Rendering.ScriptableRenderContext,
                        Il2CppReferenceArray<Camera>>(
                        (ctx, cams) => Once(ref _frameTick, "RenderPipelineManager.beginFrameRendering")));

                var existing = UnityEngine.Rendering.RenderPipelineManager.beginFrameRendering;
                UnityEngine.Rendering.RenderPipelineManager.beginFrameRendering =
                    Il2CppSystem.Delegate.Combine(existing, cb).Cast<FrameCallback>();
            });

            // The game uses the new Input System, whose onAfterUpdate is a real event with
            // add_/remove_ accessors (unlike the RenderPipelineManager delegate fields) and
            // fires once per frame.
            Try("InputSystem.onAfterUpdate", () =>
            {
                UnityEngine.InputSystem.InputSystem.add_onAfterUpdate(
                    DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                        new Action(() => Once(ref _inputTick, "InputSystem.onAfterUpdate"))));
            });

            Try("SceneManager.sceneLoaded", () =>
            {
                SceneManager.sceneLoaded += DelegateSupport.ConvertDelegate<
                    UnityEngine.Events.UnityAction<Scene, LoadSceneMode>>(
                    new Action<Scene, LoadSceneMode>(
                        (s, m) =>
                        {
                            Once(ref _sceneLoaded, $"SceneManager.sceneLoaded '{s.name}'");
                            InstallLate();
                        }));
            });
        }

        private static bool _lateDone, _lateInput, _lateFrame;

        /// <summary>
        /// Re-registers the per-frame hooks once a scene exists. Registering them from Load()
        /// silently does nothing: that runs before any scene, and with Class::Init stubbed out
        /// a class the game has not touched yet is never initialised, so subscribing to its
        /// static event writes into storage nothing ever reads.
        /// </summary>
        private static void InstallLate()
        {
            if (_lateDone) return;
            _lateDone = true;
            LilithModPlugin.Logger.LogInfo("[DIAG] --- re-registering per-frame hooks after scene load ---");
            DiagTickProbe.Install();

            Try("late InputSystem.onAfterUpdate", () =>
            {
                UnityEngine.InputSystem.InputSystem.add_onAfterUpdate(
                    DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(
                        new Action(() => Once(ref _lateInput, "late InputSystem.onAfterUpdate"))));
            });

            Try("late RenderPipelineManager.beginFrameRendering", () =>
            {
                var cb = DelegateSupport.ConvertDelegate<FrameCallback>(
                    new Action<UnityEngine.Rendering.ScriptableRenderContext,
                        Il2CppReferenceArray<Camera>>(
                        (ctx, cams) => Once(ref _lateFrame, "late RenderPipelineManager.beginFrameRendering")));

                var existing = UnityEngine.Rendering.RenderPipelineManager.beginFrameRendering;
                UnityEngine.Rendering.RenderPipelineManager.beginFrameRendering =
                    Il2CppSystem.Delegate.Combine(existing, cb).Cast<FrameCallback>();
            });
        }

        private static void Try(string what, Action register)
        {
            try
            {
                register();
                LilithModPlugin.Logger.LogInfo($"[DIAG] registered {what}");
            }
            catch (Exception ex)
            {
                LilithModPlugin.Logger.LogWarning($"[DIAG] could not register {what}: {ex.Message}");
            }
        }

        private static void Once(ref bool flag, string what)
        {
            if (flag) return;
            flag = true;
            LilithModPlugin.Logger.LogInfo($"[DIAG] *** FIRED: {what} ***");
        }
    }
}
