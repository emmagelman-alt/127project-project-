using UnityEngine;
using TMPro;

/// <summary>
/// The word on the root. Put this on a 3D TextMeshPro object
/// (GameObject > 3D Object > Text - TextMeshPro, NOT the UI version) and type your word into it.
/// It keeps the word on the root and fades it in and out when BoothRootController says so.
/// </summary>
[RequireComponent(typeof(TextMeshPro))]
public class RootLabel : MonoBehaviour
{
    public RootTendril tendril;
    [Tooltip("Where along the root the word sits. 0 = booth, 1 = foot.")]
    [Range(0f, 1f)] public float positionAlongRoot = 0.5f;
    [Tooltip("How fast the word fades in and out (1 = one second).")]
    public float fadeSpeed = 2f;
    [Tooltip("On: the word faces the camera. Off: it lies flat on the floor along the root.")]
    public bool faceCamera = false;
    [Tooltip("Meters. When the root is shorter than this, the word shrinks so it never sticks out past the root.")]
    public float fullSizeLength = 1.2f;

    [Header("Root under the word")]
    [Tooltip("How much the root fades where the word sits, so the word is easy to read. " +
             "0 = no fade, 1 = the root disappears under the word.")]
    [Range(0f, 1f)] public float fadeRootUnderWord = 1f;
    [Tooltip("Extra meters of faded root past each end of the word.")]
    public float fadeMargin = 0.03f;
    [Tooltip("Meters over which the root fades back in at each end, so the edges are soft.")]
    public float fadeSoftness = 0.08f;

    TextMeshPro text;
    Vector3 baseScale;
    bool visible;

    void Awake()
    {
        text = GetComponent<TextMeshPro>();
        text.alpha = 0f;
        baseScale = transform.localScale;
    }

    public void SetVisible(bool v) { visible = v; }

    void LateUpdate()
    {
        text.alpha = Mathf.MoveTowards(text.alpha, visible ? 1f : 0f, fadeSpeed * Time.deltaTime);
        if (!tendril) return;
        if (text.alpha <= 0f)
        {
            tendril.ClearFade();
            return;
        }

        float t = positionAlongRoot;
        Vector3 nearBooth = tendril.PointAt(Mathf.Clamp01(t - 0.05f));
        Vector3 nearFoot = tendril.PointAt(Mathf.Clamp01(t + 0.05f));
        Vector3 towardBooth = nearBooth - nearFoot;

        transform.position = tendril.PointAt(t) + Vector3.up * 0.005f;

        if (faceCamera && Camera.main)
        {
            transform.position += Vector3.up * 0.1f;
            transform.rotation = Camera.main.transform.rotation;
        }
        else if (towardBooth.sqrMagnitude > 0.000001f)
        {
            // Lie flat on the floor, reading along the root toward the booth.
            transform.rotation = Quaternion.LookRotation(Vector3.down, Vector3.Cross(Vector3.down, towardBooth));
        }

        transform.localScale = baseScale * Mathf.Clamp01(tendril.Length / fullSizeLength);

        FadeRootUnderWord(t, towardBooth);
    }

    // Fades the stretch of root the word covers, more as the word fades in.
    void FadeRootUnderWord(float t, Vector3 towardBooth)
    {
        float rootLength = tendril.Length;
        Bounds word = text.textBounds; // the word's size and middle, in the text's own space
        if (fadeRootUnderWord <= 0f || rootLength < 0.001f || word.size.x <= 0f ||
            towardBooth.sqrMagnitude < 0.000001f)
        {
            tendril.ClearFade();
            return;
        }

        Vector3 alongRoot = -towardBooth.normalized; // pointing toward the foot

        // How far the word reaches along the root on each side of its middle, in meters.
        Vector3 halfWidth = transform.TransformVector(new Vector3(word.extents.x, 0f, 0f));
        Vector3 halfHeight = transform.TransformVector(new Vector3(0f, word.extents.y, 0f));
        float halfLength = Mathf.Abs(Vector3.Dot(halfWidth, alongRoot)) +
                           Mathf.Abs(Vector3.Dot(halfHeight, alongRoot));

        // The word's middle can sit a little off its pivot, depending on its alignment.
        float middleT = t + Vector3.Dot(transform.TransformVector(word.center), alongRoot) / rootLength;

        tendril.FadeAround(middleT, halfLength + fadeMargin, fadeSoftness, fadeRootUnderWord * text.alpha);
    }
}
