// Add this to your FollowRig script or create a new one
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Canvas))]
public class AlwaysOnTopUI : MonoBehaviour
{
    private Canvas _canvas;
    private int _originalSortingOrder;
    
    private void Awake()
    {
        _canvas = GetComponent<Canvas>();
        _originalSortingOrder = _canvas.sortingOrder;
    }

    private void Update()
    {
        // Set to a high value to ensure it renders on top
        _canvas.sortingOrder = 1000;
        
        // Optional: Force update the canvas
        Canvas.ForceUpdateCanvases();
    }
}