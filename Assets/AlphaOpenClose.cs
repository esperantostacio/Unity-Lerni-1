using System.Collections;
using UnityEngine;

public class AlphaOpenClose : MonoBehaviour
{
    [Header("References")]
    [Tooltip("CanvasGroup to fade in/out")]
    [SerializeField] private CanvasGroup targetCanvasGroup;

    [Header("Timings")]
    [Tooltip("Fade-in duration in seconds")]
    [SerializeField] private float fadeInDuration = 1.4f;

    [Tooltip("Fade-out duration in seconds")]
    [SerializeField] private float fadeOutDuration = 1.0f;

    private Coroutine _fadeCoroutine;

    private void OnEnable()
    {
        if (targetCanvasGroup == null)
            return;

        StopFadeIfRunning();
        targetCanvasGroup.alpha = 0f;
        _fadeCoroutine = StartCoroutine(FadeCanvasGroup(targetCanvasGroup, 0f, 1f, fadeInDuration));
    }

    private void OnDisable()
    {
        if (targetCanvasGroup == null)
            return;

        StopFadeIfRunning();

        // Use a runner so the fade-out can continue even if this component is disabled.
        AlphaOpenCloseRunner.Instance.StartCoroutine(FadeCanvasGroup(targetCanvasGroup, targetCanvasGroup.alpha, 0f, fadeOutDuration));
    }

    private void StopFadeIfRunning()
    {
        if (_fadeCoroutine != null)
        {
            StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = null;
        }
    }

    private static IEnumerator FadeCanvasGroup(CanvasGroup canvasGroup, float from, float to, float duration)
    {
        if (canvasGroup == null)
            yield break;

        float elapsed = 0f;
        canvasGroup.alpha = from;

        if (duration <= 0f)
        {
            canvasGroup.alpha = to;
            yield break;
        }

        while (elapsed < duration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            canvasGroup.alpha = Mathf.Lerp(from, to, t);
            yield return null;
        }

        canvasGroup.alpha = to;
    }

    private sealed class AlphaOpenCloseRunner : MonoBehaviour
    {
        private static AlphaOpenCloseRunner _instance;

        public static AlphaOpenCloseRunner Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("AlphaOpenCloseRunner");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<AlphaOpenCloseRunner>();
                }

                return _instance;
            }
        }
    }
}
