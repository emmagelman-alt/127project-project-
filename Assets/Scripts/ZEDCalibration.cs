using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

/// <summary>
/// Lines up the ZED camera's body tracking with the scene, so a tracked person's feet land
/// where the projector draws the floor (and where the booths' roots reach for them).
///
/// The ZED already finds the floor by itself (height and tilt). What it can't know is where
/// the camera stands in the scene and which way it faces. This works that out from a few
/// Calibration Points: a person stands on each one in the real room, and the ZED rig is
/// turned and shifted until the feet it sees land on the points.
///
/// It also measures the projector's size. If the projector shows the scene smaller than real
/// life (say 4 m of scene fits on 1 m of floor), the ZED's measurements are stretched by the
/// same amount, so a real step matches a step in the picture. Nothing in the scene needs
/// resizing. (Match Projector Scale; this sets the scale of the object it moves.)
///
/// SETUP (once, on any computer)
///   1. Make an empty object, call it "ZED Calibration", and add this script.
///      Three Calibration Points appear as its children.
///   2. Move the points onto the scene floor, spread across the area people walk in:
///      for example one near each booth and two far apart. The farther apart, the better.
///   3. Leave ZED Rig empty. It finds the ZED camera rig by itself and moves the object
///      the rig sits under (the "ZED" object in the lab template).
///
/// AT THE LAB (again whenever the camera or the projector gets moved)
///   Line up the projector first: the points are shown on the real floor through it.
///   Click the Game view so it gets the keys, then:
///     C           start. The points light up as rings on the floor.
///                 Hands-free (on by default, for calibrating alone): walk to the bright ring
///                 before its countdown runs out, hold still while it captures, and it moves on
///                 to the next ring by itself.
///     Space       with ONE person standing on the bright ring, capture it now.
///                 They hold still for about 2 seconds.
///     N           skip this point.        Backspace   redo the previous point.
///   After the last point the fix is worked out and applied, and it shows how far off
///   each point still is. Then:
///     I J K L     nudge everyone up / left / down / right on screen (hold Shift for bigger steps)
///     U  O        turn everyone a little
///     Enter       save.        Esc   cancel and go back to the saved calibration.
///   The saved calibration loads by itself every time you press Play.
/// </summary>
public class ZEDCalibration : MonoBehaviour
{
    [Header("Points")]
    [Tooltip("Spots on the scene floor that a person can stand on in the real room. " +
             "Use 2 or more; 3 or 4 spread far apart works best. Keep them on the scene's floor, " +
             "since the floor height is taken from them too.")]
    public List<Transform> points = new List<Transform>();

    [Header("ZED")]
    [Tooltip("The object that gets moved to line up the ZED. Leave empty to use the object " +
             "the ZED camera rig (ZEDManager) sits under.")]
    public Transform zedRig;
    [Tooltip("Also move the ZED's floor up or down to the height of the points, so the tracked " +
             "people stand on the scene's floor.")]
    public bool matchFloorHeight = true;
    [Tooltip("Also measure how much smaller or bigger the projector shows the scene than real life, and " +
             "stretch the ZED's measurements by that much, so a step on the real floor is as long as a step " +
             "in the projected picture. Needs 2 or more points (3 or more to check the result).")]
    public bool matchProjectorScale = true;

    [Header("Capture")]
    [Tooltip("Calibrate alone: after C, each ring captures by itself on a timer, so nobody has to press " +
             "Space. Walk to the bright ring, stand still while it counts down, then go to the next one.")]
    public bool handsFree = true;
    [Tooltip("Hands-free: seconds to walk to each ring before it starts capturing.")]
    public float walkSeconds = 6f;
    [Tooltip("Hands-free: extra seconds for the first ring, to get from the keyboard to the floor.")]
    public float extraSecondsFirstRing = 6f;
    [Tooltip("Seconds the person holds still on each point.")]
    public float captureSeconds = 2f;
    [Tooltip("If the tracked feet wobble more than this (in cm) while someone stands still on a " +
             "point, the point isn't kept and you're asked to capture it again.")]
    public float maxWobbleCm = 30f;
    [Tooltip("Size of the rings drawn on the floor, in meters on the real floor " +
             "(once the projector's size is known; before the first calibration, in scene meters).")]
    public float ringRadius = 0.25f;
    [Tooltip("Width of the rings' lines, in meters, like Ring Radius.")]
    public float ringWidth = 0.04f;

    [Header("Saving")]
    [Tooltip("The calibration is saved under this name in the app's data folder " +
             "(the exact path is printed to the Console when saving).")]
    public string fileName = "zed_calibration.json";
    [Tooltip("Shows a short note at the start of Play saying whether a calibration was loaded.")]
    public bool showStartNote = true;

    /// <summary>What gets written to the file.</summary>
    [Serializable]
    public class SavedCalibration
    {
        public float x, y, z, yaw;
        public float scale = 1f; // scene meters per real meter (how much the ZED's measurements are stretched)
        public int pointsUsed;
        public float averageErrorCm;
        public string savedAt;
    }

    enum Mode { Off, Capturing, Sampling, Review }

    struct Fit
    {
        public bool ok;
        public float yaw;          // degrees around the vertical axis
        public Vector3 position;   // where the ZED rig object goes
        public float scale;        // scene meters per real meter (1 when the scale isn't matched)
        public float rmsCm;        // average leftover error, in cm on the real floor
        public float scaleOffPct;  // how much farther apart the points are in the scene than for real
        public bool scaleLooksWrong;
        public float rejectedScale;
        public bool looksMirrored;
    }

    Mode mode = Mode.Off;
    int current;                          // the point being captured
    Vector3?[] captured = new Vector3?[0]; // feet seen for each point, in the ZED rig's own space
    bool[] skipped = new bool[0];
    float[] spreadCm = new float[0];
    readonly List<Vector3> samples = new List<Vector3>();
    float sampleTime;
    float walkTimeLeft, walkTimeTotal; // hands-free: time left to get to the ring
    bool retrying;                     // hands-free: the last capture wobbled, so it's trying again
    string sampleWarning = "";
    Fit fit;

    Pose poseBeforeCalibrating;
    Pose poseAtStart;
    Vector3 scaleBeforeCalibrating = Vector3.one;
    Vector3 scaleAtStart = Vector3.one;
    string startNote = "";
    float startNoteUntil;

    readonly List<LineRenderer> rings = new List<LineRenderer>();
    readonly List<LineRenderer> dots = new List<LineRenderer>();
    readonly List<LineRenderer> feetMarks = new List<LineRenderer>();
    readonly List<LineRenderer> liveMarks = new List<LineRenderer>(); // where the camera sees people right now
    Material ringMaterial;
    GUIStyle boxStyle, floorStyle;
    LineRenderer timerArc; // hands-free: shrinks while walking to the ring, fills while holding still

    // The ZED's list of the people it is tracking (found by name, so this script doesn't depend on the plugin).
    MonoBehaviour bodyTracker;
    System.Reflection.PropertyInfo trackedListProperty;
    bool usingZEDList;
    readonly List<Vector3> trackedFeet = new List<Vector3>();

    string SavePath => Path.Combine(Application.persistentDataPath, fileName);

    // Scene meters per real meter, as the ZED rig is set right now.
    float ProjectorScale => zedRig ? Mathf.Max(Mathf.Abs(zedRig.lossyScale.x), 0.0001f) : 1f;

    // Stretches the ZED's measurements across the floor only; heights stay real, so people keep
    // their real height and the ZED still finds the floor.
    void SetScale(float k) => zedRig.localScale = new Vector3(k, 1f, k);

    // ----------------------------------------------------------------------------------------
    // Start: find the rig and load the saved calibration

    void Start()
    {
        if (!zedRig) zedRig = FindZEDRig();
        if (!zedRig)
        {
            Debug.LogWarning("[ZED Calibration] Couldn't find the ZED camera rig. " +
                             "Drag the ZED object onto ZED Rig.", this);
            enabled = false;
            return;
        }
        poseAtStart = new Pose(zedRig.position, zedRig.rotation);
        scaleAtStart = zedRig.localScale;
        FindBodyTracker();
        Load();
    }

    void FindBodyTracker()
    {
        foreach (MonoBehaviour mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
        {
            if (mb.GetType().Name != "ZEDBodyTrackingManager") continue;
            bodyTracker = mb;
            trackedListProperty = mb.GetType().GetProperty("AvatarControlList");
            if (trackedListProperty == null)
                Debug.LogWarning("[ZED Calibration] The ZED body tracking manager has no AvatarControlList " +
                                 "(different plugin version?), so every humanoid in the scene counts as a person.", this);
            return;
        }
    }

    // The ZED moves its camera rig (ZEDManager) inside its parent, and places the tracked
    // people from the rig's position. So moving the parent moves the camera and the people together.
    static Transform FindZEDRig()
    {
        foreach (MonoBehaviour mb in FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude))
        {
            if (mb.GetType().Name != "ZEDManager") continue;
            Transform rig = mb.transform;
            if (rig.parent) return rig.parent;

            // No parent to move: give it one.
            Transform holder = new GameObject("ZED (calibrated)").transform;
            rig.SetParent(holder, true);
            return holder;
        }
        return null;
    }

    void Load()
    {
        string path = SavePath;
        if (!File.Exists(path))
        {
            ShowStartNote("No ZED calibration saved on this computer yet. Press C to calibrate.");
            return;
        }
        try
        {
            SavedCalibration s = JsonUtility.FromJson<SavedCalibration>(File.ReadAllText(path));
            zedRig.SetPositionAndRotation(new Vector3(s.x, s.y, s.z), Quaternion.Euler(0f, s.yaw, 0f));
            float k = s.scale > 0.01f ? s.scale : 1f;
            if (matchProjectorScale) SetScale(k);
            string size = matchProjectorScale && Mathf.Abs(k - 1f) > 0.05f ? $", 1 m of floor = {k:F2} m of scene" : "";
            ShowStartNote($"ZED calibration loaded (saved {s.savedAt}, {s.pointsUsed} points, " +
                          $"about {s.averageErrorCm:F0} cm off{size}). Press C to calibrate again.");
            Debug.Log($"[ZED Calibration] Loaded from {path}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ZED Calibration] Couldn't read {path}: {e.Message}", this);
        }
    }

    void Save()
    {
        Vector3 p = zedRig.position;
        var s = new SavedCalibration
        {
            x = p.x, y = p.y, z = p.z,
            yaw = zedRig.eulerAngles.y,
            scale = ProjectorScale,
            pointsUsed = CapturedCount(),
            averageErrorCm = CurrentRmsCm(),
            savedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
        };
        try
        {
            File.WriteAllText(SavePath, JsonUtility.ToJson(s, true));
            Debug.Log($"[ZED Calibration] Saved to {SavePath}", this);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[ZED Calibration] Couldn't save to {SavePath}: {e.Message}", this);
        }
    }

    [ContextMenu("Forget the saved calibration")]
    void ForgetSaved()
    {
        if (File.Exists(SavePath)) File.Delete(SavePath);
        if (Application.isPlaying && zedRig)
        {
            zedRig.SetPositionAndRotation(poseAtStart.position, poseAtStart.rotation);
            zedRig.localScale = scaleAtStart;
        }
        Debug.Log("[ZED Calibration] Saved calibration deleted.", this);
    }

    void ShowStartNote(string note)
    {
        if (!showStartNote) return;
        startNote = note;
        startNoteUntil = Time.unscaledTime + 8f;
    }

    // ----------------------------------------------------------------------------------------
    // Keys and steps

    void Update()
    {
        if (Down(K.C)) { StartCalibrating(); return; }
        if (mode == Mode.Off) return;

        if (Down(K.Escape)) { Cancel(); return; }

        RefreshTrackedPeople();
        switch (mode)
        {
            case Mode.Capturing:
                if (Down(K.Space)) BeginSampling();
                else if (Down(K.N)) { skipped[current] = true; NextPoint(); }
                else if (Down(K.Backspace)) PreviousPoint();
                else if (handsFree)
                {
                    walkTimeLeft -= Time.unscaledDeltaTime;
                    if (walkTimeLeft <= 0f) BeginSampling();
                }
                break;

            case Mode.Sampling:
                Sample();
                break;

            case Mode.Review:
                if (Down(K.Enter)) { Save(); Close(); return; }
                if (Down(K.Backspace))
                {
                    current = LastPointIndex();
                    captured[current] = null;
                    GoToPoint(walkSeconds);
                    break;
                }
                Nudge();
                break;
        }
        UpdateRings();
    }

    void StartCalibrating()
    {
        points.RemoveAll(p => !p);
        if (points.Count == 0)
        {
            Debug.LogWarning("[ZED Calibration] Add some Calibration Points first.", this);
            return;
        }
        if (mode == Mode.Off)
        {
            poseBeforeCalibrating = new Pose(zedRig.position, zedRig.rotation);
            scaleBeforeCalibrating = zedRig.localScale;
        }

        captured = new Vector3?[points.Count];
        skipped = new bool[points.Count];
        spreadCm = new float[points.Count];
        current = 0;
        sampleWarning = "";
        fit = default;
        GoToPoint(walkSeconds + extraSecondsFirstRing);
        startNoteUntil = 0f;
        MakeRings();
    }

    // Light up the current point's ring and, hands-free, give the person time to get there.
    void GoToPoint(float secondsToGetThere)
    {
        mode = Mode.Capturing;
        walkTimeTotal = walkTimeLeft = Mathf.Max(secondsToGetThere, 0f);
        retrying = false;
    }

    void Cancel()
    {
        zedRig.SetPositionAndRotation(poseBeforeCalibrating.position, poseBeforeCalibrating.rotation);
        zedRig.localScale = scaleBeforeCalibrating;
        Close();
    }

    void Close()
    {
        mode = Mode.Off;
        DestroyRings();
    }

    void BeginSampling()
    {
        samples.Clear();
        sampleTime = 0f;
        sampleWarning = "";
        mode = Mode.Sampling;
    }

    void Sample()
    {
        // Exactly one person may be tracked. Before calibrating, the camera's people show up in
        // the wrong places, so there's no telling which of several is the one on the ring.
        if (trackedFeet.Count != 1)
        {
            samples.Clear();
            sampleTime = 0f; // start over once it's down to one person
            sampleWarning = trackedFeet.Count == 0
                ? "Waiting: the camera isn't tracking anyone. Is the person in its view?"
                : $"Paused: the camera is tracking {trackedFeet.Count} people (the pink circles). " +
                  "Only the person on the ring may be in its view.";
            return;
        }
        sampleWarning = "";

        samples.Add(zedRig.InverseTransformPoint(trackedFeet[0]));
        sampleTime += Time.unscaledDeltaTime;
        if (sampleTime < captureSeconds) return;

        // The middle of what was seen, ignoring the odd jump, and how much it wobbled.
        Vector3 middle = Median(samples);
        float wobbleCm = Wobble(samples, middle) * 100f;
        if (wobbleCm > maxWobbleCm)
        {
            GoToPoint(handsFree ? 3f : 0f); // hands-free: try again by itself in a moment
            retrying = true;
            sampleWarning = $"Not kept: the tracked feet wobbled about {wobbleCm:F0} cm while they stood still. " +
                            (handsFree ? "Trying again: stand still in the ring." : "Press Space to try again.") +
                            " If it keeps happening, make sure the camera sees the whole body.";
            return;
        }

        captured[current] = middle;
        spreadCm[current] = wobbleCm;
        NextPoint();
    }

    static Vector3 Median(List<Vector3> values)
    {
        var xs = values.ConvertAll(v => v.x);
        var ys = values.ConvertAll(v => v.y);
        var zs = values.ConvertAll(v => v.z);
        xs.Sort();
        ys.Sort();
        zs.Sort();
        int m = values.Count / 2;
        return new Vector3(xs[m], ys[m], zs[m]);
    }

    // How far the feet strayed from the middle, ignoring the worst 20% (brief tracking glitches).
    static float Wobble(List<Vector3> values, Vector3 middle)
    {
        var d = values.ConvertAll(v => Flat(v - middle).magnitude);
        d.Sort();
        return d[Mathf.Clamp(Mathf.CeilToInt(d.Count * 0.8f) - 1, 0, d.Count - 1)];
    }

    void NextPoint()
    {
        current++;
        if (current < points.Count)
        {
            GoToPoint(walkSeconds);
            return;
        }
        fit = Solve();
        if (fit.ok)
        {
            zedRig.SetPositionAndRotation(fit.position, Quaternion.Euler(0f, fit.yaw, 0f));
            if (matchProjectorScale) SetScale(fit.scale);
        }
        mode = Mode.Review;
        RefreshTrackedPeople(); // so the pink circles jump to the new spots right away
    }

    void PreviousPoint()
    {
        if (current == 0) return;
        current--;
        captured[current] = null;
        skipped[current] = false;
        GoToPoint(walkSeconds);
    }

    int LastPointIndex()
    {
        for (int i = points.Count - 1; i >= 0; i--)
            if (captured[i].HasValue || skipped[i]) { skipped[i] = false; return i; }
        return 0;
    }

    // Small fixes by hand, in screen directions of the camera showing the Game view.
    void Nudge()
    {
        float step = (Held(K.Shift) ? 0.10f : 0.01f) * ProjectorScale; // 1 or 10 cm on the real floor
        float turn = Held(K.Shift) ? 2f : 0.25f;    // degrees

        Vector3 right = Vector3.right, up = Vector3.forward;
        Camera cam = ViewCamera();
        if (cam)
        {
            right = Flat(cam.transform.right);
            up = Flat(cam.transform.up);
            if (up.sqrMagnitude < 0.01f) up = Flat(cam.transform.forward); // a camera looking straight ahead
            right = right.sqrMagnitude > 0.0001f ? right.normalized : Vector3.right;
            up = up.sqrMagnitude > 0.0001f ? up.normalized : Vector3.forward;
        }

        Vector3 move = Vector3.zero;
        if (Down(K.I)) move += up;
        if (Down(K.K)) move -= up;
        if (Down(K.L)) move += right;
        if (Down(K.J)) move -= right;
        if (move != Vector3.zero) zedRig.position += move * step;

        if (Down(K.U)) zedRig.RotateAround(PointsCenter(), Vector3.up, -turn);
        if (Down(K.O)) zedRig.RotateAround(PointsCenter(), Vector3.up, turn);
    }

    Vector3 PointsCenter()
    {
        Vector3 c = Vector3.zero;
        int n = 0;
        foreach (Transform p in points) if (p) { c += p.position; n++; }
        return n > 0 ? c / n : zedRig.position;
    }

    // The feet of everyone the ZED camera is tracking right now. Only the ZED's own list counts,
    // so a test character standing in the scene (like the keyboard Player) is ignored.
    // Without ZED body tracking in the scene (testing on a Mac), every humanoid counts.
    void RefreshTrackedPeople()
    {
        trackedFeet.Clear();
        usingZEDList = false;
        if (bodyTracker && trackedListProperty != null &&
            trackedListProperty.GetValue(bodyTracker) is System.Collections.IDictionary list)
        {
            usingZEDList = true;
            foreach (System.Collections.DictionaryEntry e in list)
            {
                Animator anim = AnimatorOf(e.Value);
                // A newly seen person's avatar stays hidden for a moment before it appears.
                if (anim && anim.gameObject.activeInHierarchy && anim.isHuman) trackedFeet.Add(FeetOf(anim));
            }
            return;
        }
        foreach (Animator anim in FindObjectsByType<Animator>(FindObjectsInactive.Exclude))
            if (anim.isHuman) trackedFeet.Add(FeetOf(anim));
    }

    static Animator AnimatorOf(object skeletonHandler)
    {
        if (skeletonHandler == null) return null;
        var getAnimator = skeletonHandler.GetType().GetMethod("GetAnimator", Type.EmptyTypes);
        return getAnimator?.Invoke(skeletonHandler, null) as Animator;
    }

    // The point between a character's feet.
    static Vector3 FeetOf(Animator anim)
    {
        Transform l = anim.GetBoneTransform(HumanBodyBones.LeftFoot);
        Transform r = anim.GetBoneTransform(HumanBodyBones.RightFoot);
        return l && r ? (l.position + r.position) * 0.5f : l ? l.position : r ? r.position : anim.transform.position;
    }

    // ----------------------------------------------------------------------------------------
    // The math: the turn and shift that best moves the captured feet onto the points

    Fit Solve()
    {
        var from = new List<Vector2>(); // feet, in the ZED rig's own space
        var to = new List<Vector2>();   // the points, in the scene
        float floorY = 0f;
        for (int i = 0; i < points.Count; i++)
        {
            if (!captured[i].HasValue) continue;
            from.Add(XZ(captured[i].Value));
            to.Add(XZ(points[i].position));
            floorY += points[i].position.y;
        }
        int n = from.Count;
        if (n == 0) return default;
        floorY /= n;

        bool withScale = matchProjectorScale;
        Fit f = n == 1
            ? FitShiftOnly(from[0], to[0], zedRig.eulerAngles.y, withScale ? ProjectorScale : 1f)
            : FitTurnAndShift(from, to, withScale);

        // A size difference this big means the feet weren't on the rings, not a real projector size.
        if (n >= 2 && withScale && (f.scale < 0.2f || f.scale > 10f))
        {
            float measured = f.scale;
            withScale = false;
            f = FitTurnAndShift(from, to, false);
            f.scaleLooksWrong = true;
            f.rejectedScale = measured;
        }

        f.position.y = matchFloorHeight ? floorY : zedRig.position.y;

        if (n >= 3)
        {
            // If the points fit much better as a mirror image, the ZED's Mirror Mode is probably on.
            var flipped = from.ConvertAll(p => new Vector2(-p.x, p.y));
            Fit m = FitTurnAndShift(flipped, to, withScale);
            f.looksMirrored = f.rmsCm > 10f && m.rmsCm < f.rmsCm * 0.5f;
        }
        return f;
    }

    static Fit FitShiftOnly(Vector2 from, Vector2 to, float yaw, float scale)
    {
        Vector2 shift = to - Turn(from, yaw) * scale;
        return new Fit { ok = true, yaw = yaw, scale = scale, position = new Vector3(shift.x, 0f, shift.y) };
    }

    // Best turn around the vertical axis plus a shift on the floor (2D Procrustes / Kabsch),
    // and with withScale, the best stretch too (Umeyama).
    static Fit FitTurnAndShift(List<Vector2> from, List<Vector2> to, bool withScale)
    {
        int n = from.Count;
        Vector2 cf = Vector2.zero, ct = Vector2.zero;
        for (int i = 0; i < n; i++) { cf += from[i]; ct += to[i]; }
        cf /= n;
        ct /= n;

        // Unity turns (x, z) by yaw into (x cos + z sin, -x sin + z cos).
        float c = 0f, s = 0f, spreadFrom = 0f, spreadTo = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector2 a = from[i] - cf, b = to[i] - ct;
            c += a.x * b.x + a.y * b.y;
            s += a.y * b.x - a.x * b.y;
            spreadFrom += a.sqrMagnitude;
            spreadTo += b.sqrMagnitude;
        }
        float yaw = Mathf.Atan2(s, c) * Mathf.Rad2Deg;
        float k = withScale && spreadFrom > 1e-6f ? Mathf.Sqrt(c * c + s * s) / spreadFrom : 1f;
        Vector2 shift = ct - Turn(cf, yaw) * k;

        float err = 0f;
        for (int i = 0; i < n; i++) err += (Turn(from[i], yaw) * k + shift - to[i]).sqrMagnitude;

        return new Fit
        {
            ok = true,
            yaw = Mathf.Repeat(yaw, 360f),
            scale = k,
            position = new Vector3(shift.x, 0f, shift.y),
            rmsCm = Mathf.Sqrt(err / n) * 100f / Mathf.Max(k, 0.0001f),
            scaleOffPct = spreadFrom > 0.25f ? (Mathf.Sqrt(spreadTo / spreadFrom) - 1f) * 100f : 0f,
        };
    }

    static Vector2 Turn(Vector2 p, float yawDeg)
    {
        float r = yawDeg * Mathf.Deg2Rad, c = Mathf.Cos(r), s = Mathf.Sin(r);
        return new Vector2(p.x * c + p.y * s, -p.x * s + p.y * c);
    }

    static Vector2 XZ(Vector3 v) => new Vector2(v.x, v.z);
    static Vector3 Flat(Vector3 v) { v.y = 0f; return v; }

    // Where a captured spot lands with the rig where it is now, and how far that is from its point,
    // in cm on the real floor.
    float ErrorCm(int i) => Flat(zedRig.TransformPoint(captured[i].Value) - points[i].position).magnitude * 100f / ProjectorScale;

    float CurrentRmsCm()
    {
        float sum = 0f;
        int n = 0;
        for (int i = 0; i < captured.Length && i < points.Count; i++)
        {
            if (!captured[i].HasValue) continue;
            float e = ErrorCm(i);
            sum += e * e;
            n++;
        }
        return n > 0 ? Mathf.Sqrt(sum / n) : 0f;
    }

    int CapturedCount()
    {
        int n = 0;
        foreach (Vector3? c in captured) if (c.HasValue) n++;
        return n;
    }

    // ----------------------------------------------------------------------------------------
    // Rings on the floor (seen through the projector) while calibrating

    void MakeRings()
    {
        DestroyRings();
        Shader shader = Shader.Find("Sprites/Default");
        if (!shader) shader = Shader.Find("Hidden/Internal-Colored");
        ringMaterial = new Material(shader);

        for (int i = 0; i < points.Count; i++)
        {
            rings.Add(MakeCircle($"Calibration ring {i + 1}", 64));
            dots.Add(MakeCircle($"Calibration dot {i + 1}", 24));
            feetMarks.Add(MakeCircle($"Calibration feet {i + 1}", 24));
        }
        timerArc = MakeCircle("Calibration timer", 65);
        timerArc.loop = false;
        timerArc.enabled = false;
    }

    // Part of a circle, clockwise from the top: 1 = the whole way round.
    static void PlaceArc(LineRenderer lr, Vector3 center, float radius, float width, Color color, float fraction)
    {
        fraction = Mathf.Clamp01(fraction);
        if (fraction < 0.005f) { lr.enabled = false; return; }
        lr.enabled = true;
        lr.startWidth = lr.endWidth = width;
        lr.startColor = lr.endColor = color;
        int n = Mathf.Max(2, Mathf.CeilToInt(64 * fraction) + 1);
        lr.positionCount = n;
        for (int k = 0; k < n; k++)
        {
            float a = Mathf.PI * 0.5f - fraction * Mathf.PI * 2f * k / (n - 1);
            lr.SetPosition(k, center + new Vector3(Mathf.Cos(a) * radius, 0.02f, Mathf.Sin(a) * radius));
        }
    }

    LineRenderer MakeCircle(string name, int segments)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        lr.sharedMaterial = ringMaterial;
        lr.useWorldSpace = true;
        lr.loop = true;
        lr.positionCount = segments;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows = false;
        lr.numCornerVertices = 2;
        lr.sortingOrder = 100;
        return lr;
    }

    static void PlaceCircle(LineRenderer lr, Vector3 center, float radius, float width, Color color)
    {
        lr.enabled = true;
        lr.startWidth = lr.endWidth = width;
        lr.startColor = lr.endColor = color;
        int n = lr.positionCount;
        for (int k = 0; k < n; k++)
        {
            float a = k * Mathf.PI * 2f / n;
            lr.SetPosition(k, center + new Vector3(Mathf.Cos(a) * radius, 0.02f, Mathf.Sin(a) * radius));
        }
    }

    void UpdateRings()
    {
        float pulse = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 6f);
        float size = ProjectorScale; // so the rings are Ring Radius wide on the real floor
        float ringRadius = this.ringRadius * size, ringWidth = this.ringWidth * size;
        for (int i = 0; i < rings.Count && i < points.Count; i++)
        {
            if (!points[i]) continue;
            Vector3 c = points[i].position;
            bool active = (mode == Mode.Capturing || mode == Mode.Sampling) && i == current;
            Color color =
                active ? Color.Lerp(new Color(1f, 0.85f, 0.1f), Color.white, pulse) :
                captured[i].HasValue ? new Color(0.2f, 1f, 0.35f) :
                skipped[i] ? new Color(1f, 0.3f, 0.3f, 0.6f) :
                new Color(1f, 1f, 1f, 0.45f);
            float radius = active ? ringRadius * (1f + 0.15f * pulse) : ringRadius;
            float width = active ? ringWidth * 1.6f : ringWidth;

            PlaceCircle(rings[i], c, radius, width, color);
            PlaceCircle(dots[i], c, ringRadius * 0.2f, width, color);

            // After solving: where the captured feet land now. They should sit inside the ring.
            if (mode == Mode.Review && captured[i].HasValue)
                PlaceCircle(feetMarks[i], Flat(zedRig.TransformPoint(captured[i].Value)) + Vector3.up * c.y,
                            ringRadius * 0.35f, ringWidth * 0.75f, new Color(0.2f, 0.8f, 1f));
            else
                feetMarks[i].enabled = false;
        }

        // Hands-free timer around the bright ring: yellow and shrinking while walking there,
        // blue and filling up while holding still.
        if (timerArc)
        {
            if (FloorLabel() != null)
            {
                bool walking = mode == Mode.Capturing;
                float fraction = walking
                    ? (walkTimeTotal > 0f ? walkTimeLeft / walkTimeTotal : 0f)
                    : sampleTime / Mathf.Max(captureSeconds, 0.01f);
                PlaceArc(timerArc, points[current].position, ringRadius * 1.5f, ringWidth * 1.8f,
                         walking ? new Color(1f, 0.85f, 0.1f) : new Color(0.2f, 0.8f, 1f), fraction);
            }
            else timerArc.enabled = false;
        }

        // Pink circles: where the camera sees each person right now. Before calibrating they can be
        // far from the real person; afterwards they should be right under their feet.
        float floorY = PointsCenter().y;
        while (liveMarks.Count < trackedFeet.Count) liveMarks.Add(MakeCircle($"Tracked person {liveMarks.Count + 1}", 24));
        for (int i = 0; i < liveMarks.Count; i++)
        {
            if (i < trackedFeet.Count)
                PlaceCircle(liveMarks[i], Flat(trackedFeet[i]) + Vector3.up * floorY,
                            ringRadius * 0.5f, ringWidth, new Color(1f, 0.25f, 0.85f));
            else
                liveMarks[i].enabled = false;
        }
    }

    void DestroyRings()
    {
        foreach (LineRenderer lr in rings) if (lr) Destroy(lr.gameObject);
        foreach (LineRenderer lr in dots) if (lr) Destroy(lr.gameObject);
        foreach (LineRenderer lr in feetMarks) if (lr) Destroy(lr.gameObject);
        foreach (LineRenderer lr in liveMarks) if (lr) Destroy(lr.gameObject);
        if (timerArc) Destroy(timerArc.gameObject);
        timerArc = null;
        rings.Clear();
        dots.Clear();
        feetMarks.Clear();
        liveMarks.Clear();
        if (ringMaterial) Destroy(ringMaterial);
    }

    void OnDisable() => DestroyRings();

    // ----------------------------------------------------------------------------------------
    // On-screen instructions (on the main display)

    void OnGUI()
    {
        string text = Instructions();
        if (string.IsNullOrEmpty(text)) return;

        if (boxStyle == null)
        {
            boxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 18,
                alignment = TextAnchor.UpperLeft,
                wordWrap = true,
                richText = true,
                padding = new RectOffset(14, 14, 10, 10),
            };
            boxStyle.normal.textColor = Color.white;
        }
        float w = Mathf.Min(620f, Screen.width - 20f);
        float h = boxStyle.CalcHeight(new GUIContent(text), w);
        GUI.Box(new Rect(10f, 10f, w, h), text, boxStyle);
        DrawFloorLabel();
    }

    // Hands-free: what to show in big letters next to the bright ring, or null for nothing.
    string FloorLabel()
    {
        if (!handsFree || current >= points.Count || !points[current]) return null;
        if (mode == Mode.Capturing)
        {
            int seconds = Mathf.CeilToInt(Mathf.Max(walkTimeLeft, 0f));
            return retrying ? $"again in {seconds}" : seconds.ToString();
        }
        if (mode == Mode.Sampling)
            return trackedFeet.Count == 1 ? "hold still" : trackedFeet.Count == 0 ? "can't see you" : "only 1 person";
        return null;
    }

    // Big enough to read standing on the floor, beside the ring (on the left if there's no room on the right).
    void DrawFloorLabel()
    {
        string text = FloorLabel();
        Camera cam = ViewCamera();
        if (text == null || !cam) return;

        Vector3 center = points[current].position;
        Vector3 sp = cam.WorldToScreenPoint(center);
        if (sp.z < 0f) return;
        float gap = Mathf.Abs(cam.WorldToScreenPoint(center + cam.transform.right * ringRadius * ProjectorScale * 1.7f).x - sp.x) + 8f;

        if (floorStyle == null)
        {
            floorStyle = new GUIStyle(GUI.skin.label) { fontSize = 56, fontStyle = FontStyle.Bold, wordWrap = false };
            floorStyle.normal.textColor = Color.white;
        }
        const float width = 520f, height = 80f;
        float y = Screen.height - sp.y - height * 0.5f;
        bool right = sp.x + gap + width <= Screen.width;
        floorStyle.alignment = right ? TextAnchor.MiddleLeft : TextAnchor.MiddleRight;
        var rect = new Rect(right ? sp.x + gap : sp.x - gap - width, y, width, height);

        Color before = GUI.color;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.Label(new Rect(rect.x + 3f, rect.y + 3f, width, height), text, floorStyle); // shadow
        GUI.color = before;
        GUI.Label(rect, text, floorStyle);
    }

    // The camera drawing the Game view (what the projector shows): the frontmost one on Display 1.
    static Camera ViewCamera()
    {
        Camera best = null;
        foreach (Camera c in Camera.allCameras)
            if (!c.targetTexture && c.targetDisplay == 0 && (!best || c.depth > best.depth)) best = c;
        return best ? best : Camera.main;
    }

    string Instructions()
    {
        switch (mode)
        {
            case Mode.Off:
                return Time.unscaledTime < startNoteUntil ? startNote : null;

            case Mode.Capturing:
                if (handsFree)
                    return $"<b>ZED calibration: point {current + 1} of {points.Count}</b> (hands-free)\n" +
                           "Walk to the bright ring and stand in the middle, facing any way. It starts capturing in " +
                           $"<b>{Mathf.CeilToInt(Mathf.Max(walkTimeLeft, 0f))} s</b>; then hold still for {captureSeconds:0.#} s " +
                           "and it moves on to the next ring by itself. Nobody else in the camera's view.\n" +
                           "<b>Space</b> capture now    <b>N</b> skip this point    <b>Backspace</b> back    <b>Esc</b> cancel\n" +
                           PeopleLine() + Warning(sampleWarning);
                return $"<b>ZED calibration: point {current + 1} of {points.Count}</b>\n" +
                       "Have ONE person stand in the middle of the bright ring, facing any way. " +
                       "Everyone else stays out of the camera's view.\n" +
                       "<b>Space</b> capture    <b>N</b> skip this point    <b>Backspace</b> back    <b>Esc</b> cancel\n" +
                       PeopleLine() + Warning(sampleWarning);

            case Mode.Sampling:
                return $"<b>Capturing point {current + 1} of {points.Count}</b>: hold still... " +
                       $"{Mathf.Clamp01(sampleTime / captureSeconds) * 100f:F0}%\n" + PeopleLine() + Warning(sampleWarning);

            case Mode.Review:
                return ReviewText() + "\n" + PeopleLine();
        }
        return null;
    }

    string PeopleLine()
    {
        string who = trackedFeet.Count == 1 ? "1 person" : $"{trackedFeet.Count} people";
        string source = usingZEDList ? "The camera is tracking" : "No ZED body tracking found; humanoids in the scene:";
        return $"{source} <b>{who}</b> (pink circles show where it sees them).";
    }

    string ReviewText()
    {
        var t = new System.Text.StringBuilder();
        int n = CapturedCount();
        if (n == 0)
            return "<b>No points were captured.</b>\n<b>C</b> start again    <b>Esc</b> cancel";

        t.Append("<b>ZED calibration done.</b> The blue circles show where the captured feet land now; " +
                 "they should sit inside the rings.\n");
        for (int i = 0; i < points.Count; i++)
        {
            if (!captured[i].HasValue) { t.Append($"   point {i + 1}: skipped\n"); continue; }
            t.Append($"   point {i + 1}: {ErrorCm(i):F0} cm off");
            if (spreadCm[i] > 15f) t.Append($"  <color=#ffcc44>(the feet wobbled {spreadCm[i]:F0} cm while capturing)</color>");
            t.Append('\n');
        }
        float rms = CurrentRmsCm();
        t.Append($"Average: <b>{rms:F0} cm</b>" +
                 (rms <= 10f ? "  (good)" : rms <= 20f ? "  (OK)" : "  <color=#ffcc44>(poor: try again)</color>") + "\n");

        if (n == 1) t.Append(Warning("Only 1 point: the position is fixed but the direction isn't. Use 2 or more points."));
        if (n == 2 && matchProjectorScale)
            t.Append(Warning("Only 2 points: they always fit exactly, so there's nothing to check the result against. Use 3 or more."));

        if (fit.scaleLooksWrong)
            t.Append(Warning($"The size difference came out as {fit.rejectedScale:F2}x, which can't be right, " +
                             "so it wasn't used. Check that the person stood in the middle of each bright ring."));
        else if (matchProjectorScale && n >= 2 && Mathf.Abs(fit.scale - 1f) > 0.05f)
            t.Append($"\nSize: 1 m on the real floor = <b>{fit.scale:F2} m</b> in the scene (the projector shows the scene " +
                     $"{(fit.scale > 1f ? "smaller" : "bigger")} than real life). The camera's measurements are " +
                     $"{(fit.scale > 1f ? "stretched" : "shrunk")} to match, so nothing in the scene needs resizing.");
        else if (!matchProjectorScale && Mathf.Abs(fit.scaleOffPct) > 5f)
            t.Append(Warning($"The points are {Mathf.Abs(fit.scaleOffPct):F0}% {(fit.scaleOffPct > 0 ? "farther apart" : "closer together")} " +
                             "in the scene than on the real floor. The projector's picture is probably " +
                             $"{(fit.scaleOffPct > 0 ? "too small" : "too big")}: turn on Match Projector Scale, " +
                             "or fix the projector camera's size, then calibrate again."));
        if (fit.looksMirrored)
            t.Append(Warning("The points fit much better as a mirror image. Check that Mirror Mode is off " +
                             "on the ZED body tracking manager."));

        t.Append("\n<b>Enter</b> save    <b>I J K L</b> nudge (Shift = bigger)    <b>U O</b> turn    " +
                 "<b>Backspace</b> redo last point    <b>C</b> start again    <b>Esc</b> cancel");
        return t.ToString();
    }

    static string Warning(string w) => string.IsNullOrEmpty(w) ? "" : $"\n<color=#ffcc44>{w}</color>";

    // ----------------------------------------------------------------------------------------
    // Keys (works with the new Input System and the old Input Manager)

    enum K { C, Space, N, Backspace, Enter, Escape, I, J, K, L, U, O, Shift }

    static bool Down(K key)
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb == null) return false;
        switch (key)
        {
            case K.Enter: return kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
            case K.Shift: return kb.shiftKey.wasPressedThisFrame;
            default: return kb[ToKey(key)].wasPressedThisFrame;
        }
#else
        if (key == K.Enter) return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter);
        if (key == K.Shift) return Input.GetKeyDown(KeyCode.LeftShift) || Input.GetKeyDown(KeyCode.RightShift);
        return Input.GetKeyDown(ToKeyCode(key));
#endif
    }

    static bool Held(K key)
    {
#if ENABLE_INPUT_SYSTEM
        Keyboard kb = Keyboard.current;
        if (kb == null) return false;
        return key == K.Shift ? kb.shiftKey.isPressed : kb[ToKey(key)].isPressed;
#else
        if (key == K.Shift) return Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
        return Input.GetKey(ToKeyCode(key));
#endif
    }

#if ENABLE_INPUT_SYSTEM
    static Key ToKey(K key)
    {
        switch (key)
        {
            case K.C: return Key.C;
            case K.Space: return Key.Space;
            case K.N: return Key.N;
            case K.Backspace: return Key.Backspace;
            case K.Escape: return Key.Escape;
            case K.I: return Key.I;
            case K.J: return Key.J;
            case K.K: return Key.K;
            case K.L: return Key.L;
            case K.U: return Key.U;
            case K.O: return Key.O;
            default: return Key.None;
        }
    }
#else
    static KeyCode ToKeyCode(K key)
    {
        switch (key)
        {
            case K.C: return KeyCode.C;
            case K.Space: return KeyCode.Space;
            case K.N: return KeyCode.N;
            case K.Backspace: return KeyCode.Backspace;
            case K.Escape: return KeyCode.Escape;
            case K.I: return KeyCode.I;
            case K.J: return KeyCode.J;
            case K.K: return KeyCode.K;
            case K.L: return KeyCode.L;
            case K.U: return KeyCode.U;
            case K.O: return KeyCode.O;
            default: return KeyCode.None;
        }
    }
#endif

    // ----------------------------------------------------------------------------------------
    // Editor: three starting points when the script is added, and rings in the Scene view

#if UNITY_EDITOR
    void Reset()
    {
        if (points.Count > 0) return;
        Vector3[] spots = { new Vector3(-1.5f, 0f, -1f), new Vector3(1.5f, 0f, -1f), new Vector3(0f, 0f, 1.5f) };
        foreach (Vector3 s in spots) AddPointAt(transform.position + s);
    }

    [ContextMenu("Add a calibration point")]
    void AddPoint()
    {
        Vector3 at = points.Count > 0 && points[points.Count - 1]
            ? points[points.Count - 1].position + new Vector3(1f, 0f, 0f)
            : transform.position;
        AddPointAt(at);
    }

    void AddPointAt(Vector3 position)
    {
        var go = new GameObject($"Calibration Point {points.Count + 1}");
        UnityEditor.Undo.RegisterCreatedObjectUndo(go, "Add calibration point");
        go.transform.SetParent(transform, false);
        go.transform.position = position;
        UnityEditor.Undo.RecordObject(this, "Add calibration point");
        points.Add(go.transform);
    }

    void OnDrawGizmos()
    {
        for (int i = 0; i < points.Count; i++)
        {
            if (!points[i]) continue;
            Vector3 c = points[i].position;
            UnityEditor.Handles.color = new Color(1f, 0.85f, 0.1f);
            UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius);
            UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius * 0.2f);
            UnityEditor.Handles.Label(c + Vector3.up * 0.1f, $"Calibration Point {i + 1}");
        }
    }
#endif
}
