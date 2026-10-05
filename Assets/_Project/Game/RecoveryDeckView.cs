using System.Collections.Generic;
using UnityEngine;
using Kare.Space.Core;

namespace Kare.Space.Game
{
    /// <summary>
    /// Посадочная баржа в море (§6.9, RecoveryDef.AtSea с палубой над водой — OCISLY у Falcon 9). Только вид: настил для
    /// ядра — круг DeckRadius на высоте DeckHeight (BoosterLandingAutopilot.DeckUnder), здесь он же — коробкой корпуса.
    /// Одна баржа на цель на всю сцену: виды ступени до и после отделения — разные VesselView, а баржа одна.
    /// </summary>
    public sealed class RecoveryDeckView : MonoBehaviour
    {
        /// <summary>
        /// Палуба OCISLY ≈ 52 × 91 м (по X — поперёк, по Z — вдоль), осадка корпуса под водой, м. Пара: DeckRadius 45 у
        /// SpaceXRockets.Ocisly — круг ядра чуть шире полуширины палубы, касание у кромки засчитывается и тут видно на краю.
        /// </summary>
        const float DeckWidth = 52, DeckLength = 91, Draft = 4;
        /// <summary>Отбойные стенки вдоль кормы и бортов, высота и толщина, м.</summary>
        const float WallHeight = 3, WallThick = 1.5f;
        /// <summary>Дальше этого баржу не рисуем — меньше пикселя.</summary>
        const float DrawDistance = 60000;

        static readonly Dictionary<string, RecoveryDeckView> Decks = new Dictionary<string, RecoveryDeckView>();

        CelestialBody body;
        Vector3d anchorBf;
        QuaternionD frameBf;
        Renderer[] renderers;

        /// <summary>Баржа для цели def на теле body; уже есть — ничего не делает.</summary>
        public static void Ensure(CelestialBody body, RecoveryDef def, Material baseMat)
        {
            if (body == null || def == null || !def.AtSea || def.DeckHeight <= 0) return;
            string key = def.TargetName ?? "deck";
            if (Decks.TryGetValue(key, out var d) && d != null) return;
            var go = new GameObject("Recovery Deck " + key);
            d = go.AddComponent<RecoveryDeckView>();
            d.Init(body, def, baseMat);
            Decks[key] = d;
        }

        void Init(CelestialBody b, RecoveryDef def, Material baseMat)
        {
            body = b;
            // Базис: X — восток, Y — зенит, Z — север (как у LaunchPadView), начало — на уровне моря.
            var up = CelestialBody.LatLonToBodyFixed(def.TargetLat, def.TargetLon);
            anchorBf = up * body.Radius;
            var east = Vector3d.Cross(Vector3d.forward, up).normalized;
            var north = Vector3d.Cross(up, east);
            frameBf = QuaternionD.FromBasis(east.SwapYZ, up.SwapYZ, north.SwapYZ);

            var shader = baseMat != null ? baseMat.shader : Shader.Find("HDRP/Lit");
            float deck = (float)def.DeckHeight, hw = DeckWidth * 0.5f, hl = DeckLength * 0.5f;
            Part("Hull", new Color(0.16f, 0.17f, 0.18f), shader,
                (new Vector3(-hw, -Draft, -hl), new Vector3(hw, deck - 0.2f, hl)));
            Part("Deck", new Color(0.36f, 0.36f, 0.35f), shader,
                (new Vector3(-hw, deck - 0.2f, -hl), new Vector3(hw, deck, hl)));
            Part("Walls", new Color(0.12f, 0.12f, 0.12f), shader,
                (new Vector3(-hw, deck, -hl), new Vector3(-hw + WallThick, deck + WallHeight, hl)),
                (new Vector3(hw - WallThick, deck, -hl), new Vector3(hw, deck + WallHeight, hl)),
                (new Vector3(-hw, deck, -hl), new Vector3(hw, deck + WallHeight, -hl + WallThick)));
            // Мишень посадки: круг ядра не нарисовать коробками — крест по центру палубы.
            Part("Mark", new Color(0.9f, 0.9f, 0.88f), shader,
                (new Vector3(-12, deck, -1), new Vector3(12, deck + 0.05f, 1)),
                (new Vector3(-1, deck, -12), new Vector3(1, deck + 0.05f, 12)));
            renderers = GetComponentsInChildren<Renderer>();
            LateUpdate();
        }

        void Part(string name, Color color, Shader shader, params (Vector3 min, Vector3 max)[] boxes)
        {
            var mat = new Material(shader) { name = "Deck " + name };
            mat.SetColor("_BaseColor", color);
            mat.SetFloat("_Smoothness", 0.25f);
            foreach (var (min, max) in boxes)
            {
                var go = new GameObject(name);
                go.transform.SetParent(transform, false);
                go.transform.localPosition = (min + max) * 0.5f;
                go.transform.localScale = max - min;
                go.AddComponent<MeshFilter>().sharedMesh = ProcMesh.Box();
                go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            }
        }

        void LateUpdate()
        {
            if (body == null) return;
            var o = body.Orientation;
            var pos = FloatingOrigin.ToUnity(body.Position + o * anchorBf);
            bool show = !MapView.IsOpen && FlightView.Near(pos, DrawDistance);
            foreach (var r in renderers) r.enabled = show;
            if (show) transform.SetPositionAndRotation(pos, FloatingOrigin.ToQuaternion(o.SwapYZ * frameBf));
        }
    }
}
