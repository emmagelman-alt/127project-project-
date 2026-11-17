using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

/// <summary>
/// The glowing root. Put this on an empty GameObject (a LineRenderer is added for you).
/// Draws a flower's root on the floor from a point on the booth's ring to the person's foot:
/// pale and smooth, fuzzy with root hairs, with thin side roots branching off it.
/// It grows out, shrinks back, and pulses once it is holding on.
/// BoothRootController tells it what to do; it doesn't need anything else.
/// </summary>
[RequireComponent(typeof(LineRenderer))]
public class RootTendril : MonoBehaviour
{
    [Header("Shape")]
    [Range(8, 96)] public int points = 48;
    [Tooltip("How far the root bends sideways, as a fraction of its length.")]
    public float wiggle = 0.12f;
    [Tooltip("Roughly how many bends along the root.")]
    public float bends = 2f;
    [Tooltip("Small kinks along the root, as a fraction of its length. 0 = smooth curves.")]
    public float kinks = 0.025f;
    [Tooltip("How fast the bends drift over time. 0 = still.")]
    public float wiggleSpeed = 0.3f;
    [Tooltip("Meters above the floor, so the root doesn't flicker into it.")]
    public float floorOffset = 0.01f;

    [Header("Root look")]
    [Tooltip("Paints a flower-root pattern onto the root when the game starts: pale, finely streaked, " +
             "with a lighter core. Turn off to use the material's own texture instead.")]
    [FormerlySerializedAs("paintBark")] public bool paintRoot = true;
    [Tooltip("Fuzzy root hairs along the root's edges. They sit outside the Width you set, " +
             "so the root itself stays that thick.")]
    public bool rootHairs = true;
    [Tooltip("Size of the pattern. 1 = sized to the root's width. Bigger = finer, smaller = coarser.")]
    [FormerlySerializedAs("barkScale")] public float patternScale = 1f;
    [Tooltip("How uneven the root's thickness is. 0 = perfectly smooth.")]
    [Range(0f, 1f)] public float knobbiness = 0.12f;
    [Tooltip("Roughly how many meters apart the thicker bits are.")]
    public float knobSpacing = 0.25f;
    [Tooltip("On: the root is see-through at its edges and fades to nothing under the word. " +
             "Off: the material is drawn exactly as it is set up.")]
    public bool seeThrough = true;

    [Header("Side roots")]
    [Tooltip("The most thin side roots that branch off the main root. 0 = none.")]
    [Range(0, 24)] public int sideRoots = 12;
    [Tooltip("Meters between side roots along the main root.")]
    public float sideRootSpacing = 0.14f;
    [Tooltip("Meters. How long the side roots grow, on average.")]
    public float sideRootLength = 0.2f;
    [Tooltip("How thick the side roots are compared with the main root next to them.")]
    [Range(0.1f, 1f)] public float sideRootThickness = 0.35f;

    [Header("Growth (meters per second)")]
    public float growSpeed = 1.5f;
    public float retractSpeed = 2.5f;

    [Header("Glow")]
    [Tooltip("The root's color. Its glow is the same color.")]
    public Color glowColor = new Color(0.75f, 0.68f, 0.58f, 1f); // pale flower-root cream
    [Tooltip("Brightness. Above 1 is what Bloom turns into a glow.")]
    public float glow = 1.4f;
    [Tooltip("How much the brightness rises and falls while pulsing. Small = faint pulse.")]
    public float pulse = 0.3f;
    public float pulseSpeed = 2.5f;

    LineRenderer line;
    Material mat;
    int colorId;
    int texId = -1; // the material's main texture slot, or -1 if it has none
    Vector3 a, b;   // a = point on the ring, b = foot
    float progress; // 0 = hidden, 1 = touching the foot
    int dir;        // +1 growing, -1 shrinking back, 0 still
    bool pulsing;
    float seed;
    Vector3[] pts;        // the points drawn this frame
    float[] distAlong;    // meters from the booth to each drawn point
    float drawnLength;    // meters of root drawn this frame

    // Width along the root
    AnimationCurve baseWidth;             // the Width curve set on the LineRenderer in the Inspector
    readonly AnimationCurve widthNow = new AnimationCurve();
    const int WidthKeys = 48;
    readonly Keyframe[] widthKeys = new Keyframe[WidthKeys];
    readonly float[] widths = new float[WidthKeys];

    // Side roots
    struct SideRoot
    {
        public LineRenderer line;
        public float at;      // meters from the booth along the main root
        public float side;    // +1 or -1: which side it grows out of
        public float angle;   // radians away from the main root's direction
        public float curl;    // radians it bends by its tip
        public float length;  // meters when fully grown
        public float seed;
    }
    SideRoot[] sides;
    const int SidePoints = 10;
    readonly Vector3[] sidePts = new Vector3[SidePoints];
    bool sidesFaded;

    // Fading the root under the word (RootLabel asks for it)
    Gradient baseGradient;                // the Color set on the LineRenderer in the Inspector
    readonly Gradient fadeGradient = new Gradient();
    readonly GradientColorKey[] fadeColors = new GradientColorKey[6];
    readonly GradientAlphaKey[] fadeAlphas = new GradientAlphaKey[6];
    readonly float[] fadeTimes = new float[6];
    bool faded;

    static Texture2D rootTexture; // made once and shared by every root

    public bool FullyGrown => progress >= 1f;
    public bool FullyRetracted => progress <= 0f;
    public float Length => Vector3.Distance(a, b);

    public void SetEnds(Vector3 anchor, Vector3 foot) { a = anchor; b = foot; }
    public void Grow() { dir = 1; line.enabled = true; }
    public void Retract() { dir = -1; }
    public void SetPulsing(bool on) { pulsing = on; }

    bool Hairy => paintRoot && rootHairs && texId != -1;

    // Runs when you first add this component in the editor: sensible starting settings.
    void Reset()
    {
        LineRenderer lr = GetComponent<LineRenderer>();
        if (!lr) return;
        lr.widthMultiplier = 0.06f;
        lr.widthCurve = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(1f, 0.3f)); // thick at booth, thin at foot
        lr.numCapVertices = 4;
        lr.textureMode = LineTextureMode.Tile; // texture repeats instead of squashing as the root shortens
        lr.alignment = LineAlignment.TransformZ;
        lr.shadowCastingMode = ShadowCastingMode.Off;
    }

    void Awake()
    {
        line = GetComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.alignment = LineAlignment.TransformZ;
        transform.rotation = Quaternion.Euler(90f, 0f, 0f); // makes the ribbon lie flat on the floor
        line.positionCount = points;
        line.enabled = false;
        line.shadowCastingMode = ShadowCastingMode.Off;
        line.sortingOrder = -1; // drawn before the word, so the word is always on top

        mat = line.material; // this root's own copy, so changing its color doesn't affect others
        if (mat)
        {
            UseWorkingShader();
            if (seeThrough) MakeSeeThrough();
            colorId = Shader.PropertyToID(mat.HasProperty("_BaseColor") ? "_BaseColor" : "_Color");
            texId = mat.HasProperty("_BaseMap") ? Shader.PropertyToID("_BaseMap")
                  : mat.HasProperty("_MainTex") ? Shader.PropertyToID("_MainTex")
                  : -1;
        }
        seed = Random.value * 100f;
        baseGradient = line.colorGradient;
        baseWidth = line.widthCurve;

        if (paintRoot && texId != -1)
        {
            if (!rootTexture) rootTexture = MakeRootTexture();
            mat.SetTexture(texId, rootTexture);
            line.textureMode = LineTextureMode.Tile;
        }

        MakeSideRoots();
    }

    void Update()
    {
        if (!line.enabled) return;

        float speed = dir > 0 ? growSpeed : retractSpeed;
        progress = Mathf.Clamp01(progress + dir * speed * Time.deltaTime / Mathf.Max(Length, 0.01f));
        if (dir < 0 && progress <= 0f)
        {
            line.enabled = false;
            HideSideRoots();
            return;
        }

        // Draw only the first `progress` fraction of the path. This is what makes it grow and shrink back.
        if (pts == null || pts.Length != points)
        {
            pts = new Vector3[points];
            distAlong = new float[points];
        }
        for (int i = 0; i < points; i++)
            pts[i] = PointAt(progress * i / (points - 1f));
        line.positionCount = points;
        line.SetPositions(pts);

        // Measure along the root as drawn, because it bends.
        drawnLength = 0f;
        distAlong[0] = 0f;
        for (int i = 1; i < points; i++)
        {
            drawnLength += Vector3.Distance(pts[i - 1], pts[i]);
            distAlong[i] = drawnLength;
        }

        UpdateWidth();
        UpdateSideRoots();

        if (mat)
        {
            float intensity = glow + (pulsing ? pulse * Mathf.Sin(Time.time * pulseSpeed) : 0f);
            Color c = glowColor * intensity;
            c.a = glowColor.a;
            mat.SetColor(colorId, c);

            // One pattern every (8 x root width) meters, so it isn't stretched or squashed.
            if (paintRoot && texId != -1)
            {
                float fullWidth = line.widthMultiplier * (Hairy ? 1f / BodyFraction : 1f);
                mat.SetTextureScale(texId, new Vector2(patternScale / (8f * Mathf.Max(fullWidth, 0.01f)), 1f));
            }
        }
    }

    /// <summary>A point on the full root path. t = 0 at the booth, 1 at the foot.
    /// The ends stay pinned and the middle bends.</summary>
    public Vector3 PointAt(float t)
    {
        Vector3 along = b - a;
        Vector3 side = Vector3.Cross(Vector3.up, along).normalized;
        float drift = Time.time * wiggleSpeed;
        float n = (Mathf.PerlinNoise(seed, t * bends + drift) - 0.5f) * 2f * wiggle
                + (Mathf.PerlinNoise(seed + 50f, t * bends * 3f + drift) - 0.5f) * 2f * kinks;
        Vector3 p = Vector3.Lerp(a, b, t) + side * (n * along.magnitude * Mathf.Sin(t * Mathf.PI));
        p.y = a.y + floorOffset;
        return p;
    }

    // The root's width along its length: the Width set in the Inspector, a little uneven,
    // and wider by the root hairs. The uneven bits are measured from the booth,
    // so they stay put near the booth while the foot end grows and shrinks.
    void UpdateWidth()
    {
        float hairs = Hairy ? 1f / BodyFraction : 1f;
        float spacing = Mathf.Max(knobSpacing, 0.02f);
        for (int k = 0; k < WidthKeys; k++)
        {
            float frac = k / (WidthKeys - 1f);
            float swell = 0f;
            if (knobbiness > 0f)
            {
                float n = Mathf.PerlinNoise(seed + 37.1f, frac * drawnLength / spacing);
                swell = Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.4f, 0.8f, n));
            }
            widths[k] = baseWidth.Evaluate(frac) * hairs * (1f + knobbiness * swell);
        }

        float step = 1f / (WidthKeys - 1f);
        for (int k = 0; k < WidthKeys; k++)
        {
            float before = widths[Mathf.Max(k - 1, 0)];
            float after = widths[Mathf.Min(k + 1, WidthKeys - 1)];
            float slope = (after - before) / (step * (k == 0 || k == WidthKeys - 1 ? 1f : 2f));
            widthKeys[k] = new Keyframe(k * step, widths[k], slope, slope);
        }
        widthNow.keys = widthKeys;
        line.widthCurve = widthNow;
    }

    // ---------- Side roots ----------

    void MakeSideRoots()
    {
        int count = Mathf.Clamp(sideRoots, 0, 24);
        sides = new SideRoot[count];
        var rng = new System.Random(Mathf.FloorToInt(seed * 1000f));
        float R() => (float)rng.NextDouble();

        var taper = new AnimationCurve(new Keyframe(0f, 1f), new Keyframe(0.6f, 0.55f), new Keyframe(1f, 0.12f));
        float side = R() < 0.5f ? 1f : -1f;
        for (int i = 0; i < count; i++)
        {
            var go = new GameObject("Side root " + (i + 1));
            go.layer = gameObject.layer;
            go.transform.SetParent(transform, false); // takes the root's flat-on-the-floor rotation
            var lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = true;
            lr.alignment = LineAlignment.TransformZ;
            lr.sharedMaterial = mat; // same look, color and glow as the main root
            lr.textureMode = LineTextureMode.Tile;
            lr.numCapVertices = 2;
            lr.positionCount = SidePoints;
            lr.widthCurve = taper;
            lr.shadowCastingMode = ShadowCastingMode.Off;
            lr.receiveShadows = false;
            lr.lightProbeUsage = LightProbeUsage.Off;
            lr.reflectionProbeUsage = ReflectionProbeUsage.Off;
            lr.sortingOrder = line.sortingOrder - 1; // tucked under the main root
            lr.startColor = lr.endColor = Color.white;
            lr.enabled = false;

            // Mostly alternating sides, sometimes two in a row on one side.
            if (R() < 0.8f) side = -side;
            sides[i] = new SideRoot
            {
                line = lr,
                at = Mathf.Max(sideRootSpacing, 0.03f) * (i + 0.6f + (R() - 0.5f) * 0.7f),
                side = side,
                angle = Mathf.Lerp(0.55f, 1.25f, R()),
                curl = Mathf.Lerp(-0.7f, 0.35f, R()),
                length = Mathf.Lerp(0.55f, 1.3f, R()),
                seed = R() * 100f,
            };
        }
    }

    // Side roots sprout once the main root has grown a little past them, and stay clear of its tip.
    void UpdateSideRoots()
    {
        if (sides == null) return;
        float tipClear = Mathf.Max(0.08f, sideRootLength * 0.6f);
        float drift = Time.time * wiggleSpeed;

        for (int i = 0; i < sides.Length; i++)
        {
            SideRoot s = sides[i];
            float grown = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01((drawnLength - tipClear - s.at) / 0.25f));
            if (grown <= 0.001f || sideRootLength <= 0f)
            {
                s.line.enabled = false;
                continue;
            }

            Along(s.at, out Vector3 start, out Vector3 forward, out float frac);
            Vector3 across = Vector3.Cross(Vector3.up, forward).normalized * s.side;
            float step = s.length * sideRootLength * grown / (SidePoints - 1);
            Vector3 p = start;
            p.y = a.y + floorOffset * 0.5f; // a hair below the main root, so it tucks under it
            for (int k = 0; k < SidePoints; k++)
            {
                sidePts[k] = p;
                float u = k / (SidePoints - 1f);
                float ang = s.angle + s.curl * u + (Mathf.PerlinNoise(s.seed, u * 2.5f + drift) - 0.5f) * 0.8f;
                p += (forward * Mathf.Cos(ang) + across * Mathf.Sin(ang)) * step;
            }
            s.line.SetPositions(sidePts);
            s.line.widthMultiplier = line.widthMultiplier * widthNow.Evaluate(frac) * sideRootThickness;
            s.line.enabled = true;
        }
    }

    void HideSideRoots()
    {
        if (sides == null) return;
        foreach (SideRoot s in sides) s.line.enabled = false;
    }

    // The point `meters` from the booth along the drawn root, which way the root runs there,
    // and how far along the drawn root that is (0 to 1).
    void Along(float meters, out Vector3 point, out Vector3 forward, out float frac)
    {
        int n = pts.Length;
        int i = 0;
        while (i < n - 2 && distAlong[i + 1] < meters) i++;
        float segment = Mathf.Max(distAlong[i + 1] - distAlong[i], 1e-5f);
        point = Vector3.Lerp(pts[i], pts[i + 1], Mathf.Clamp01((meters - distAlong[i]) / segment));
        forward = pts[i + 1] - pts[i];
        forward.y = 0f;
        forward = forward.sqrMagnitude > 1e-10f ? forward.normalized : Vector3.forward;
        frac = drawnLength > 0f ? Mathf.Clamp01(meters / drawnLength) : 0f;
    }

    // ---------- Fading under the word ----------

    /// <summary>
    /// Fades the root around one spot, so a word sitting there is easy to read.
    /// centerT: where the spot is on the full root (0 = booth, 1 = foot).
    /// halfLength: meters of root faded on each side of the spot.
    /// soft: meters over which the root fades back in at each end.
    /// amount: 0 = no fade, 1 = the root disappears there.
    /// Call it every frame after the root has moved (RootLabel does this in LateUpdate).
    /// </summary>
    public void FadeAround(float centerT, float halfLength, float soft, float amount)
    {
        amount = Mathf.Clamp01(amount);
        if (amount <= 0f || pts == null || progress <= 0f || !line.enabled || drawnLength < 0.0001f)
        {
            ClearFade();
            return;
        }

        // Meters from the booth to the middle of the spot.
        int n = pts.Length;
        float f = centerT / progress * (n - 1);
        float middle;
        if (f >= 0f && f <= n - 1)
        {
            int i0 = Mathf.Min((int)f, n - 2);
            middle = Mathf.Lerp(distAlong[i0], distAlong[i0 + 1], f - i0);
        }
        else
        {
            middle = drawnLength * centerT / progress; // the spot is past the drawn part (the root is still growing)
        }

        // The fade as fractions of the line: full root, fading out, faded, fading back in, full root.
        soft = Mathf.Max(soft, 0.001f);
        halfLength = Mathf.Max(halfLength, 0f);
        float x0 = (middle - halfLength - soft) / drawnLength;
        float x1 = (middle - halfLength) / drawnLength;
        float x2 = (middle + halfLength) / drawnLength;
        float x3 = (middle + halfLength + soft) / drawnLength;
        float low = 1f - amount;

        fadeTimes[0] = 0f;
        fadeTimes[1] = Mathf.Clamp01(x0);
        fadeTimes[2] = Mathf.Clamp01(x1);
        fadeTimes[3] = Mathf.Clamp01(x2);
        fadeTimes[4] = Mathf.Clamp01(x3);
        fadeTimes[5] = 1f;

        for (int k = 0; k < fadeTimes.Length; k++)
        {
            float x = fadeTimes[k];
            float shown;
            if (x <= x0 || x >= x3) shown = 1f;
            else if (x >= x1 && x <= x2) shown = low;
            else if (x < x1) shown = Mathf.Lerp(1f, low, (x - x0) / Mathf.Max(x1 - x0, 1e-6f));
            else shown = Mathf.Lerp(low, 1f, (x - x2) / Mathf.Max(x3 - x2, 1e-6f));

            // Darker and more see-through: disappears on any floor when See Through is on,
            // and on a dark floor either way.
            Color c = baseGradient.Evaluate(x);
            fadeColors[k] = new GradientColorKey(new Color(c.r * shown, c.g * shown, c.b * shown), x);
            fadeAlphas[k] = new GradientAlphaKey(c.a * shown, x);
        }

        fadeGradient.SetKeys(fadeColors, fadeAlphas);
        line.colorGradient = fadeGradient;
        faded = true;

        FadeSideRoots(middle - halfLength, middle + halfLength, soft, low);
    }

    // Side roots that reach into the faded stretch fade with it.
    void FadeSideRoots(float from, float to, float soft, float low)
    {
        if (sides == null) return;
        foreach (SideRoot s in sides)
        {
            float reach = s.length * sideRootLength;
            float gap = Mathf.Max(from - (s.at + reach * 0.8f), (s.at - 0.02f) - to, 0f);
            float shown = Mathf.Lerp(low, 1f, Mathf.Clamp01(gap / soft));
            s.line.startColor = s.line.endColor = new Color(shown, shown, shown, shown);
        }
        sidesFaded = true;
    }

    /// <summary>Shows the whole root again after FadeAround.</summary>
    public void ClearFade()
    {
        if (faded)
        {
            line.colorGradient = baseGradient;
            faded = false;
        }
        if (sidesFaded && sides != null)
        {
            foreach (SideRoot s in sides) s.line.startColor = s.line.endColor = Color.white;
            sidesFaded = false;
        }
    }

    // ---------- Material ----------

    // A material made for URP (like RootGlow) draws pink in a project that doesn't use URP.
    // When that happens, switch this root's copy of the material to a built-in shader that works.
    void UseWorkingShader()
    {
        Shader s = mat.shader;
        bool urpShader = s && s.name.StartsWith("Universal Render Pipeline/");
        bool pink = !s || s.name == "Hidden/InternalErrorShader" || !s.isSupported ||
                    (urpShader && GraphicsSettings.currentRenderPipeline == null);
        if (!pink) return;

        Shader fallback = Shader.Find("Particles/Standard Unlit");
        if (!fallback || !fallback.isSupported) fallback = Shader.Find("Sprites/Default");
        if (!fallback) return;

        Debug.Log($"[Root] The root's material \"{mat.name}\" can't draw in this project (it would be pink), " +
                  $"so the root is using the built-in \"{fallback.name}\" shader instead.", this);
        mat.shader = fallback;
    }

    // Turns this root's copy of the material see-through (alpha blended), for URP's
    // particle shaders and the built-in ones. Other shaders are left as they are.
    void MakeSeeThrough()
    {
        if (mat.HasProperty("_Surface")) // URP
        {
            mat.SetFloat("_Surface", 1f);
            if (mat.HasProperty("_Blend")) mat.SetFloat("_Blend", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }
        else if (mat.HasProperty("_Mode")) // built-in particle and standard shaders
        {
            mat.SetFloat("_Mode", 2f); // Fade
            mat.EnableKeyword("_ALPHABLEND_ON");
        }
        else return;

        mat.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (mat.HasProperty("_SrcBlendAlpha")) mat.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        if (mat.HasProperty("_DstBlendAlpha")) mat.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.DisableKeyword("_ALPHATEST_ON");
        mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        mat.DisableKeyword("_ALPHAMODULATE_ON");
        mat.SetOverrideTag("RenderType", "Transparent");
        mat.renderQueue = (int)RenderQueue.Transparent;
    }

    // ---------- The root's pattern ----------
    // A 512 x 64 picture of a flower's root: a pale, smooth body with fine lengthwise streaks,
    // a lighter core and a faint warm thread down the middle, translucent toward its edges,
    // and fuzzy root hairs sticking out of both sides. It's pale and nearly white, so the Glow Color
    // decides the root's color. Its left and right edges match, so it repeats seamlessly along the root.
    const int TexW = 512, TexH = 64;
    const float BodyFraction = 0.6f; // how much of the ribbon's width is root; the rest is root hairs

    static Texture2D MakeRootTexture()
    {
        var rgb = new Color[TexW * TexH];
        var alpha = new float[TexW * TexH];

        // The root itself.
        for (int py = 0; py < TexH; py++)
        {
            float v = (py + 0.5f) / TexH;
            for (int px = 0; px < TexW; px++)
            {
                float x = px, y = py;
                // The outline wavers a little, so it isn't ruler-straight.
                float waver = 1f + 0.12f * (TileNoise(x, 0f, 1f / 45f, 1f, 20f) - 0.5f);
                float across = Mathf.Abs(v - 0.5f) / (BodyFraction * 0.5f) / waver; // 0 in the middle, 1 at the edge
                float body = 1f - Smooth(0.78f, 1f, across);
                if (body <= 0f) continue;

                float core = Mathf.Exp(-Sq(across / 0.55f));
                float thread = Mathf.Exp(-Sq(across / 0.14f));
                float streaks = 0.6f * TileNoise(x, y, 1f / 90f, 1f / 1.4f, 40f)
                              + 0.4f * TileNoise(x, y, 1f / 30f, 1f / 0.8f, 70f);
                float patches = TileNoise(x, y, 1f / 140f, 1f / 40f, 90f);

                float value = (0.68f + 0.32f * core) * (0.86f + 0.28f * streaks) * (0.93f + 0.14f * patches);
                rgb[py * TexW + px] = new Color(value, value * (0.98f - 0.04f * thread), value * (0.95f - 0.15f * thread));
                alpha[py * TexW + px] = body * (0.55f + 0.45f * core); // translucent toward the edges
            }
        }

        // Root hairs: fine, slightly tilted strands from each edge, fading toward their tips.
        var rng = new System.Random(11);
        float R() => (float)rng.NextDouble();
        float edge = BodyFraction * 0.5f * 0.85f;   // where hairs start, just inside the edge
        for (int s = -1; s <= 1; s += 2)
        {
            for (int h = 0; h < 140; h++)
            {
                float x0 = R() * TexW;
                float y0 = (0.5f + s * edge) * TexH;
                float tilt = (R() - 0.5f) * 1.1f;
                float room = s > 0 ? TexH - 1.5f - y0 : y0 - 1.5f;
                float len = Mathf.Min(Mathf.Lerp(0.35f, 1f, R()) * room, room / Mathf.Max(Mathf.Cos(tilt), 0.3f));
                float x1 = x0 + Mathf.Sin(tilt) * len;
                float y1 = y0 + s * Mathf.Cos(tilt) * len;
                float strength = Mathf.Lerp(0.35f, 0.8f, R());
                float shade = Mathf.Lerp(0.8f, 1f, R());
                for (int wrap = -1; wrap <= 1; wrap++)
                    DrawHair(rgb, alpha, x0 + wrap * TexW, y0, x1 + wrap * TexW, y1, strength, shade);
            }
        }

        var pixels = new Color32[TexW * TexH];
        for (int i = 0; i < pixels.Length; i++)
        {
            Color c = rgb[i];
            pixels[i] = new Color32((byte)(255f * Mathf.Clamp01(c.r)), (byte)(255f * Mathf.Clamp01(c.g)),
                                    (byte)(255f * Mathf.Clamp01(c.b)), (byte)(255f * Mathf.Clamp01(alpha[i])));
        }

        var tex = new Texture2D(TexW, TexH, TextureFormat.RGBA32, true)
        {
            name = "Flower root",
            wrapModeU = TextureWrapMode.Repeat,
            wrapModeV = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 4,
        };
        tex.SetPixels32(pixels);
        tex.Apply(true, true);
        return tex;
    }

    // Lays one soft, thin strand over the picture, fading toward its tip.
    static void DrawHair(Color[] rgb, float[] alpha, float x0, float y0, float x1, float y1, float strength, float shade)
    {
        int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(x0, x1) - 2f));
        int maxX = Mathf.Min(TexW - 1, Mathf.CeilToInt(Mathf.Max(x0, x1) + 2f));
        int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(y0, y1) - 2f));
        int maxY = Mathf.Min(TexH - 1, Mathf.CeilToInt(Mathf.Max(y0, y1) + 2f));
        if (minX > maxX || minY > maxY) return;

        float dx = x1 - x0, dy = y1 - y0;
        float len2 = Mathf.Max(dx * dx + dy * dy, 1e-4f);
        Color hair = new Color(shade, shade * 0.97f, shade * 0.93f);
        for (int py = minY; py <= maxY; py++)
        {
            for (int px = minX; px <= maxX; px++)
            {
                float cx = px + 0.5f, cy = py + 0.5f;
                float t = Mathf.Clamp01(((cx - x0) * dx + (cy - y0) * dy) / len2);
                float dist = Mathf.Sqrt(Sq(cx - (x0 + t * dx)) + Sq(cy - (y0 + t * dy)));
                float a = strength * (1f - Smooth(0.25f, 0.9f, dist)) * (1f - 0.7f * t);
                if (a <= 0f) continue;

                int i = py * TexW + px;
                float under = alpha[i];
                float outA = a + under * (1f - a);
                rgb[i] = (hair * a + rgb[i] * under * (1f - a)) / Mathf.Max(outA, 1e-4f);
                alpha[i] = outA;
            }
        }
    }

    // Noise that repeats seamlessly every TexW pixels along x.
    static float TileNoise(float x, float y, float fx, float fy, float offset)
    {
        float s = x / TexW;
        float n1 = Mathf.PerlinNoise(offset + x * fx, offset + y * fy);
        float n2 = Mathf.PerlinNoise(offset + (x - TexW) * fx, offset + y * fy);
        return Mathf.Lerp(n1, n2, s);
    }

    static float Smooth(float from, float to, float t)
    {
        t = Mathf.Clamp01((t - from) / (to - from));
        return t * t * (3f - 2f * t);
    }

    static float Sq(float f) => f * f;

    void OnDestroy()
    {
        if (mat) Destroy(mat);
    }
}
