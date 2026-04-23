using UnityEngine;
using System.Collections;

public class triggeranimationsometimes : MonoBehaviour
{
    [SerializeField] private Animator animator;
    
    private Coroutine triggerCoroutine;

    void Start()
    {
        if (animator == null)
            animator = GetComponent<Animator>();
        
        triggerCoroutine = StartCoroutine(TriggerAnimationRandomly());
    }

    private IEnumerator TriggerAnimationRandomly()
    {
        while (true)
        {
            float randomDelay = Random.Range(15f, 25f);
            yield return new WaitForSeconds(randomDelay);
            
            if (animator != null)
            {
                animator.SetTrigger("write");
                Debug.Log($"[triggeranimationsometimes] Triggered 'write' animation after {randomDelay:F1}s delay");
            }
        }
    }

    void OnDisable()
    {
        if (triggerCoroutine != null)
        {
            StopCoroutine(triggerCoroutine);
        }
    }
}
