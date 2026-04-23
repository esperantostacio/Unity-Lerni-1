using UnityEngine;

public class PlaySoundOnEnable : MonoBehaviour
{
    public AudioSource soundSource;
    public AudioClip audioClip;
    public GameObject icon1;
    public GameObject icon2;
    
    private float _lastPlayTime = -2f;
    private const float COOLDOWN_DURATION = 1.5f;
    private bool _isSoundPlaying = false;

    private void Update()
    {
        // Check if sound has finished playing
        if (_isSoundPlaying && !soundSource.isPlaying)
        {
            _isSoundPlaying = false;
            // Sound finished, revert icons
            icon1.SetActive(true);
            icon2.SetActive(false);
            Debug.Log("[PlaySoundOnEnable] Sound finished. Icon 1 active, Icon 2 inactive.");
        }
    }

    public void PlayOneShot()
    {
        // If sound is already playing, stop it and revert icons
        if (_isSoundPlaying && soundSource.isPlaying)
        {
            StopSound();
            return;
        }

        // Check if enough time has passed since last play
        if (Time.time - _lastPlayTime < COOLDOWN_DURATION)
        {
            Debug.Log($"[PlaySoundOnEnable] Sound on cooldown. Wait {COOLDOWN_DURATION - (Time.time - _lastPlayTime):F2}s more.");
            return;
        }

        if (soundSource != null && audioClip != null)
        {
            soundSource.clip = audioClip;
            soundSource.Play();
            _isSoundPlaying = true;
            _lastPlayTime = Time.time;
            
            // Toggle icons: deactivate icon 1, activate icon 2
            icon1.SetActive(false);
            icon2.SetActive(true);
            Debug.Log("[PlaySoundOnEnable] Sound playing. Icon 2 active, Icon 1 inactive.");
        }
    }

    /// <summary>
    /// Stops the currently playing sound and resets icons.
    /// Assign this to a button's OnClick or call it when closing the window.
    /// </summary>
    public void StopSound()
    {
        if (soundSource != null && soundSource.isPlaying)
            soundSource.Stop();

        _isSoundPlaying = false;

        if (icon1 != null) icon1.SetActive(true);
        if (icon2 != null) icon2.SetActive(false);

        Debug.Log("[PlaySoundOnEnable] Sound stopped. Icon 1 active, Icon 2 inactive.");
    }
}
