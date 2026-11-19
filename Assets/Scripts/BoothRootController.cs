using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Put this on the booth.
/// Finds people around the booth by itself, so there is no single "player" to assign.
/// Anyone a body-tracking camera turns into a humanoid character in the scene counts,
/// and so does anything tagged "Player" (handy for a test capsule).
///
/// When someone steps inside the grab ring, a root grows from the booth to their nearest foot.
/// It follows them, pulses, shows its word up close, and lets go at the release ring.
/// Several people can be held at once: each gets their own root (and word), copied from
/// Tendril and Label when the game starts, up to Max Roots.
///
/// A booth holds each person by one foot only. Booths share the feet between them: a foot
/// another booth is holding is skipped, so someone standing between two booths gets one
/// root on each foot. To add a booth, duplicate the Booth object.
///
/// Works on its own for testing: leave Tendril and Label empty and it only
/// draws the rings and prints what it sees to the Console.
/// </summary>
[DefaultExecutionOrder(-10)] // run before RootTendril so the roots use this frame's positions
public class BoothRootController : MonoBehaviour
{
    const float FT = 0.3048f; // feet to meters (1 Unity unit = 1 meter)

    [Header("Booth")]
    [Tooltip("Empty object at floor level, at the center of the pole.")]
    public Transform rootOrigin;
    [Tooltip("Meters. Make this a little wider than the radius of the booth top.")]
    public float ringRadius = 0.6f;

    [Header("People (anyone matching either box counts)")]
    [Tooltip("Every humanoid character in the scene counts as a person, such as the avatars " +
             "a body-tracking camera creates for each person it sees. Their feet are used.")]
    public bool countHumanoidCharacters = true;
    [Tooltip("Every object with the Person Tag counts as a person, such as a test capsule. " +
             "If it has a humanoid rig, its feet are used; otherwise its position is.")]
    public bool countTaggedObjects = true;
    [Tooltip("The tag for Count Tagged Objects. It must exist under Tags and Layers.")]
    public string personTag = "Player";
    [Tooltip("Seconds between looks for people who arrived or left. " +
             "The people the roots are holding are followed every frame either way.")]
    public float lookEverySeconds = 0.25f;

    [Header("Roots (leave empty to only test distances)")]
    [Tooltip("The first root. Extra roots for more people are copies of this one.")]
    public RootTendril tendril;
    [Tooltip("The first root's word. Each extra root gets a copy of it.")]
    public RootLabel label;
    [Tooltip("The most people the booth can hold at once, each with their own root. 1 = one at a time.")]
    [Range(1, 12)] public int maxRoots = 6;

    [Header("Distances in feet, measured from the ring")]
    public float grabFt = 5f;
    public float revealFt = 4f;
    public float releaseFt = 0.7f;
    [Tooltip("If a person backs away past this while a root is holding them, it lets go.")]
    public float letGoFt = 5.5f;
    [Tooltip("After a root lets go of someone, how much farther back than Release Ft they must step " +
             "before a root grabs them again. Keep it above 0 so the root doesn't flicker on and off. " +
             "Other people can be grabbed right away.")]
    public float reconnectFt = 0.5f;

    [Header("Debug")]
    [Tooltip("Draws a line from the ring to each person (magenta for people being held) " +
             "and prints how many people it sees to the Console.")]
    public bool showDebug = true;

    struct Person
    {
        public Transform body;
        public Transform leftFoot, rightFoot; // empty when the person has no humanoid rig
    }

    enum State { Idle, Growing, Attached, Releasing }

    // One root and its word, and who it is holding.
    class Grip
    {
        public RootTendril tendril;
        public RootLabel label;
        public State state = State.Idle;
        public Transform foot; // chosen once, when the root starts growing, so it doesn't jump between feet
        public Transform body;
        public bool Holding => state == State.Growing || state == State.Attached;
    }

    struct Candidate
    {
        public Transform foot, body;
        public float distFt;
    }

    readonly List<Person> people = new List<Person>();
    readonly HashSet<Animator> rigsAdded = new HashSet<Animator>();
    readonly List<Grip> grips = new List<Grip>();
    readonly HashSet<Transform> mustStepBack = new HashSet<Transform>(); // people just let go of, until they step back
    readonly List<Candidate> candidates = new List<Candidate>();
    float nextLook;
    bool warnedAboutTag;

    // Every foot any booth's root is holding, and which booth holds it. Shared by all booths.
    static readonly Dictionary<Transform, BoothRootController> heldFeet = new Dictionary<Transform, BoothRootController>();

    // Clears the shared list on entering Play mode, even when Unity skips reloading scripts.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ForgetHeldFeet() => heldFeet.Clear();

    bool HeldByAnotherBooth(Transform foot) =>
        heldFeet.TryGetValue(foot, out BoothRootController booth) && booth && booth != this;

    void Start()
    {
        MakeRoots();
    }

    void OnDisable()
    {
        // Let go of everyone, so the other booths can use those feet.
        foreach (Grip g in grips)
            if (g.Holding) Release(g);
    }

    // The first root is the one set up in the scene; the rest are copies of it, made now while
    // every root is still hidden, so each copy starts out clean.
    void MakeRoots()
    {
        grips.Clear();
        if (!tendril) return;

        grips.Add(new Grip { tendril = tendril, label = label });
        for (int i = 2; i <= maxRoots; i++)
        {
            RootTendril copy = Instantiate(tendril, tendril.transform.parent);
            copy.name = tendril.name + " " + i;
            RootLabel copyLabel = null;
            if (label)
            {
                copyLabel = Instantiate(label, label.transform.parent);
                copyLabel.name = label.name + " " + i;
                copyLabel.tendril = copy;
            }
            grips.Add(new Grip { tendril = copy, label = copyLabel });
        }
    }

    void Update()
    {
        if (!rootOrigin) return;

        if (Time.time >= nextLook)
        {
            FindPeople();
            nextLook = Time.time + Mathf.Max(0.05f, lookEverySeconds);
        }

        if (tendril)
        {
            if (grips.Count == 0) MakeRoots();
            RunRoots();
            GrabNewPeople();
        }
        if (showDebug) ShowDebug();
    }

    void RunRoots()
    {
        foreach (Grip g in grips)
        {
            if (g.Holding)
            {
                // The person left the camera's view, so their character disappeared.
                if (!Present(g.foot)) { Release(g); continue; }

                Measure(g.foot.position, out Vector3 floorPos, out Vector3 anchor, out float distFt);
                // Both ends pinned every frame, so the root gets shorter on its own as the person walks in.
                g.tendril.SetEnds(anchor, floorPos);

                if (g.state == State.Growing)
                {
                    if (distFt > letGoFt) Release(g);
                    else if (g.tendril.FullyGrown)
                    {
                        g.tendril.SetPulsing(true);
                        g.state = State.Attached;
                    }
                }
                else
                {
                    if (g.label) g.label.SetVisible(distFt <= revealFt);
                    if (distFt <= releaseFt || distFt > letGoFt) Release(g);
                }
            }
            else if (g.state == State.Releasing && g.tendril.FullyRetracted)
            {
                g.state = State.Idle;
            }
        }
    }

    // Everyone inside the grab ring this booth isn't holding yet gets a free root, closest people first.
    // Each root takes the person's nearest foot that no other booth is holding.
    void GrabNewPeople()
    {
        candidates.Clear();
        foreach (Person p in people)
        {
            if (!Present(p.body) || IsHeld(p.body)) continue;
            Transform foot = NearestFoot(p, out float distFt, skipFeetOtherBoothsHold: true);
            if (!foot) continue; // other booths are holding all of this person's feet

            // Someone already standing at the booth (for example, the camera only just spotted them)
            // isn't grabbed until they step back out past the release ring.
            if (distFt <= releaseFt) continue;

            // Someone a root just let go of has to step back a little before one grabs them again.
            if (mustStepBack.Contains(p.body))
            {
                if (distFt <= releaseFt + Mathf.Max(reconnectFt, 0.1f)) continue;
                mustStepBack.Remove(p.body);
            }

            if (distFt <= grabFt)
                candidates.Add(new Candidate { foot = foot, body = p.body, distFt = distFt });
        }
        if (candidates.Count == 0) return;
        candidates.Sort((x, y) => x.distFt.CompareTo(y.distFt));

        foreach (Candidate c in candidates)
        {
            Grip g = FreeGrip();
            if (g == null) break; // every root is busy

            g.foot = c.foot;
            g.body = c.body;
            heldFeet[c.foot] = this;
            Measure(c.foot.position, out Vector3 floorPos, out Vector3 anchor, out _);
            g.tendril.SetEnds(anchor, floorPos);
            g.tendril.Grow();
            g.state = State.Growing;
        }
    }

    Grip FreeGrip()
    {
        foreach (Grip g in grips)
            if (g.state == State.Idle) return g;
        return null;
    }

    bool IsHeld(Transform body)
    {
        foreach (Grip g in grips)
            if (g.Holding && g.body == body) return true;
        return false;
    }

    void Release(Grip g)
    {
        if (g.label) g.label.SetVisible(false);
        g.tendril.SetPulsing(false);
        g.tendril.Retract(); // stops following the foot and slides back to the ring
        if (g.body) mustStepBack.Add(g.body);
        // (ReferenceEquals, not "if (g.foot)", so a foot whose character was just deleted is removed too.)
        if (!ReferenceEquals(g.foot, null) && heldFeet.TryGetValue(g.foot, out BoothRootController owner) && owner == this)
            heldFeet.Remove(g.foot);
        g.foot = null;
        g.body = null;
        g.state = State.Releasing;
    }

    void FindPeople()
    {
        people.Clear();
        rigsAdded.Clear();
        mustStepBack.RemoveWhere(b => !Present(b)); // forget people who have left

        if (countHumanoidCharacters)
        {
            foreach (Animator anim in FindObjectsByType<Animator>())
                if (anim.isHuman && !rigsAdded.Contains(anim)) AddPerson(anim.transform, anim);
        }

        if (countTaggedObjects && !string.IsNullOrEmpty(personTag))
        {
            GameObject[] tagged;
            try
            {
                tagged = GameObject.FindGameObjectsWithTag(personTag);
            }
            catch (UnityException)
            {
                if (!warnedAboutTag)
                    Debug.LogWarning($"[Booth] There is no tag called \"{personTag}\". Add it under " +
                                     "Edit > Project Settings > Tags and Layers, or change Person Tag.", this);
                warnedAboutTag = true;
                return;
            }

            foreach (GameObject go in tagged)
            {
                // A tagged part of a humanoid character (the ZED avatar's hip bone is tagged Player),
                // or a tagged object with a character inside it: that's the character, counted once.
                Animator anim = HumanoidAround(go.transform);
                if (anim)
                {
                    if (!rigsAdded.Contains(anim)) AddPerson(anim.transform, anim);
                }
                else if (!TaggedParent(go.transform))
                {
                    // A plain tagged object, such as a test capsule. A tagged object inside another
                    // tagged object is part of that one, not a second person.
                    AddPerson(go.transform, null);
                }
            }
        }
    }

    void AddPerson(Transform body, Animator rig)
    {
        Person p = new Person { body = body };
        if (rig)
        {
            rigsAdded.Add(rig);
            p.leftFoot = rig.GetBoneTransform(HumanBodyBones.LeftFoot);
            p.rightFoot = rig.GetBoneTransform(HumanBodyBones.RightFoot);
        }
        people.Add(p);
    }

    // The humanoid character this object belongs to (it or a parent), or one inside it.
    static Animator HumanoidAround(Transform t)
    {
        Animator anim = t.GetComponentInParent<Animator>();
        if (anim && anim.isHuman) return anim;
        anim = t.GetComponentInChildren<Animator>();
        return anim && anim.isHuman ? anim : null;
    }

    bool TaggedParent(Transform t)
    {
        for (Transform p = t.parent; p; p = p.parent)
            if (p.CompareTag(personTag)) return true;
        return false;
    }

    // The person's foot closest to the booth (or their position, if they have no feet),
    // and how far it is from the ring in feet. When grabbing, feet other booths hold are skipped.
    Transform NearestFoot(Person p, out float distFt, bool skipFeetOtherBoothsHold = false)
    {
        Transform best = null;
        distFt = float.PositiveInfinity;
        bool hasFeet = Present(p.leftFoot) || Present(p.rightFoot);
        if (hasFeet)
        {
            Consider(p.leftFoot, skipFeetOtherBoothsHold, ref best, ref distFt);
            Consider(p.rightFoot, skipFeetOtherBoothsHold, ref best, ref distFt);
        }
        else
        {
            Consider(p.body, skipFeetOtherBoothsHold, ref best, ref distFt);
        }
        return best;
    }

    void Consider(Transform t, bool skipFeetOtherBoothsHold, ref Transform best, ref float bestFt)
    {
        if (!Present(t)) return;
        if (skipFeetOtherBoothsHold && HeldByAnotherBooth(t)) return;
        Measure(t.position, out _, out _, out float d);
        if (d < bestFt)
        {
            best = t;
            bestFt = d;
        }
    }

    // Flattens a position onto the floor and finds the nearest point on the ring.
    void Measure(Vector3 worldPos, out Vector3 floorPos, out Vector3 anchor, out float distFt)
    {
        floorPos = worldPos;
        floorPos.y = rootOrigin.position.y;
        anchor = RingPointToward(floorPos);
        distFt = Vector3.Distance(floorPos, anchor) / FT;
    }

    Vector3 RingPointToward(Vector3 p)
    {
        Vector3 c = rootOrigin.position;
        Vector3 dir = p - c;
        dir.y = 0f;
        return c + dir.normalized * ringRadius;
    }

    static bool Present(Transform t) => t && t.gameObject.activeInHierarchy;

    void ShowDebug()
    {
        int seen = 0;
        float nearestFt = float.PositiveInfinity;

        foreach (Person p in people)
        {
            if (!Present(p.body)) continue;
            Transform foot = NearestFoot(p, out float distFt);
            if (!foot) continue;
            seen++;
            nearestFt = Mathf.Min(nearestFt, distFt);
            Measure(foot.position, out Vector3 floorPos, out Vector3 anchor, out _);
            Debug.DrawLine(anchor, floorPos, IsHeld(p.body) ? Color.magenta : Color.gray);
        }

        if (Time.frameCount % 15 == 0) // about 4 times a second, so the Console doesn't flood
        {
            int holding = 0;
            foreach (Grip g in grips) if (g.Holding) holding++;
            string nearest = seen == 0
                ? "nobody found"
                : $"nearest: {nearestFt:F2} ft   zone: {Zone(nearestFt)}";
            Debug.Log($"[Booth] people: {seen}   {nearest}   roots holding: {holding} of {grips.Count}");
        }
    }

    string Zone(float d) =>
        d <= releaseFt ? "RELEASE" :
        d <= revealFt ? "SHOW WORD" :
        d <= grabFt ? "GRAB" :
        "outside";

#if UNITY_EDITOR
    // Select the booth to see these rings in the Scene tab.
    // Cyan = where roots start, yellow = grab, green = word, red = let go,
    // orange = step back past this to make a root grab the same person again.
    void OnDrawGizmosSelected()
    {
        if (!rootOrigin) return;
        Vector3 c = rootOrigin.position;
        UnityEditor.Handles.color = Color.cyan;
        UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius);
        UnityEditor.Handles.color = Color.yellow;
        UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius + grabFt * FT);
        UnityEditor.Handles.color = Color.green;
        UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius + revealFt * FT);
        UnityEditor.Handles.color = Color.red;
        UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius + releaseFt * FT);
        UnityEditor.Handles.color = new Color(1f, 0.5f, 0f);
        UnityEditor.Handles.DrawWireDisc(c, Vector3.up, ringRadius + (releaseFt + Mathf.Max(reconnectFt, 0.1f)) * FT);
    }
#endif
}
