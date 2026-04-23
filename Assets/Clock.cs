using UnityEngine;

public class Clock : MonoBehaviour
{
    [Header("Clock Hands")]
    [SerializeField] private GameObject secondHand;
    [SerializeField] private GameObject minuteHand;
    [SerializeField] private GameObject hourHand;
    [SerializeField] private float clockFaceZeroAngle = 240f;

    private void Update()
    {
        var now = System.DateTime.Now;

        float seconds = now.Second + (now.Millisecond / 1000f);
        float minutes = now.Minute + (seconds / 60f);
        float hours = (now.Hour % 12) + (minutes / 60f);

        // Clock angle: 0° at 12, increases clockwise
        // Unity Z rotation: starts at clockFaceZeroAngle for 12, increases clockwise
        // Example mapping: 6pm -> 60°, 9pm -> 150°, 12pm -> 240° (clockFaceZeroAngle = 240)
        
        float secondAngle = seconds * 6f;  // 360 / 60
        float minuteAngle = minutes * 6f;  // 360 / 60
        float hourAngle = hours * 30f;     // 360 / 12

        if (secondHand != null)
            secondHand.transform.localRotation = Quaternion.Euler(0f, 0f, clockFaceZeroAngle + secondAngle);
        
        if (minuteHand != null)
            minuteHand.transform.localRotation = Quaternion.Euler(0f, 0f, clockFaceZeroAngle + minuteAngle);
        
        if (hourHand != null)
            hourHand.transform.localRotation = Quaternion.Euler(0f, 0f, clockFaceZeroAngle + hourAngle);
    }
}
