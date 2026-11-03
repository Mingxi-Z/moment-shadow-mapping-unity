using UnityEngine;

public class DemoSphereMotion : MonoBehaviour
{
    public bool animate = true;
    [Range(0f, 1f)] public float height;
    [Min(0f)] public float minimumCenterHeight = 1.05f;
    [Min(0f)] public float maximumCenterHeight = 3f;
    [Min(0.1f)] public float period = 6f;

    private float phase;
    private float lastHeight;

    void Update()
    {
        // Resume from the selected height after a manual adjustment.
        if (!Mathf.Approximately(height, lastHeight))
            phase = Mathf.Acos(1f - 2f * Mathf.Clamp01(height)) / (2f * Mathf.PI);

        if (animate)
        {
            phase = Mathf.Repeat(phase + Time.deltaTime / Mathf.Max(period, 0.1f), 1f);
            height = 0.5f - 0.5f * Mathf.Cos(phase * 2f * Mathf.PI);
        }

        var position = transform.localPosition;
        position.y = Mathf.Lerp(minimumCenterHeight, maximumCenterHeight, height);
        transform.localPosition = position;
        lastHeight = height;
    }
}
