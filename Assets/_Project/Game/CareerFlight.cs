using System;
using System.Collections.Generic;
using System.IO;
using Kare.Space.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Kare.Space.Game
{
    /// <summary>
    /// Полёт в режиме карьеры (GDD §8.3–8.4): оплата пуска со стола, наука и возврат (CareerObserver), контракт по успеху
    /// миссии (MissionTracker) и окно результата. Вешает PauseMenu на объект GameBootstrap; в песочнице молчит.
    /// Пуск со стола — новый полёт (время = старт миссии); загруженное сохранение повторно не оплачивается.
    /// </summary>
    public sealed class CareerFlight : MonoBehaviour
    {
        /// <summary>Свежий пуск: время вселенной не дальше стольких секунд от старта миссии.</summary>
        const double FreshWindow = 1;
        const float PopupW = 520;

        enum Gate { None, Locked, Funds }

        CareerState state;
        CareerObserver observer;
        CareerContract contract;
        bool counted, ownRocket;
        double price;
        string priceWhat;
        List<string> locked = new List<string>();
        Gate gate;
        MissionStatus lastStatus = MissionStatus.Active;
        CareerReward reward;
        string resultText;
        bool resultOpen;
        GUIStyle head, text, btn;

        void Start()
        {
            var boot = GameBootstrap.Instance;
            var u = GameBootstrap.U;
            state = Career.Current;
            if (state == null || boot == null || u == null) { enabled = false; return; }
            ownRocket = GameBootstrap.NextDesign != null;
            contract = CareerContracts.ForMission(boot.Mission.Id);
            observer = new CareerObserver(state);
            observer.Message += Say;
            lastStatus = boot.Tracker.Status;
            bool fresh = Math.Abs(u.Time - boot.Mission.StartTime) < FreshWindow;
            if (!fresh) { counted = true; return; } // загрузка сохранения: пуск уже оплачен
            Price(u);
            gate = locked.Count > 0 ? Gate.Locked : state.Funds < price ? Gate.Funds : Gate.None;
            if (gate == Gate.None) Pay();
            else Time.timeScale = 0; // ракета ждёт решения на столе, время миссии не идёт
        }

        /// <summary>Своя ракета — по деталям из _last.json (его пишет конструктор перед пуском), историческая — по секциям.</summary>
        void Price(Universe u)
        {
            var v = u.MissionVessel ?? u.Active;
            price = CareerPricing.DesignPrice(v?.Design);
            priceWhat = v?.Design?.Name ?? "ракета";
            locked.Clear();
            if (!ownRocket) return;
            try
            {
                string last = Path.Combine(Application.persistentDataPath, "Crafts", "_last.json");
                if (!File.Exists(last)) return;
                var craft = Craft.FromJson(File.ReadAllText(last));
                price = CareerPricing.CraftPrice(craft);
                priceWhat = craft.Name;
                locked = CareerPricing.LockedParts(craft, state);
            }
            catch (Exception e) { Debug.LogWarning($"[Career] _last.json: {e.Message}"); }
        }

        void Pay()
        {
            state.Charge(price, priceWhat);
            CareerStore.Save();
            counted = true;
            gate = Gate.None;
            Time.timeScale = 1;
            Say($"Карьера: пуск «{priceWhat}» −{price:F0} M₢, в казне {CareerStore.Money(state.Funds)}");
        }

        void Say(string msg)
        {
            var boot = GameBootstrap.Instance;
            if (boot == null) return;
            boot.Messages.Add(msg);
            if (boot.Messages.Count > 8) boot.Messages.RemoveAt(0);
        }

        void Update()
        {
            // Игрок ушёл в песочницу посреди полёта — дальше не считаем.
            if (counted && Career.Current != state) counted = false;
            // Пока ракета ждёт на столе, игрок может изучить узлы в окне карьеры (Esc) — пересчитать допуск.
            if (gate != Gate.None)
            {
                if (Career.Current != state) { gate = Gate.None; Time.timeScale = 1; return; }
                locked.RemoveAll(state.IsPartUnlocked);
                gate = locked.Count > 0 ? Gate.Locked : state.Funds < price ? Gate.Funds : Gate.None;
                if (gate == Gate.None) Pay();
                return;
            }
            var boot = GameBootstrap.Instance;
            var u = GameBootstrap.U;
            if (!counted || boot == null || u == null) return;
            int science = state.ScienceDone.Count;
            double funds = state.Funds;
            observer.Update(u);
            var status = boot.Tracker.Status;
            if (status != lastStatus)
            {
                lastStatus = status;
                if (status == MissionStatus.Success) OnSuccess(u);
                else if (status == MissionStatus.Failed)
                    Result($"Миссия провалена: {boot.Tracker.FailReason}", "Контракт не засчитан. Деньги за пуск не возвращаются.");
            }
            if (state.ScienceDone.Count != science || state.Funds != funds) CareerStore.Save();
        }

        void OnSuccess(Universe u)
        {
            if (contract == null) { Result("Миссия выполнена", "Эта миссия — не контракт карьеры: в зачёт идёт только наука."); return; }
            string block = state.ContractBlock(contract);
            if (block != null) { Result($"Миссия выполнена: {contract.Title}", $"Контракт не засчитан: {block}."); return; }
            if (!ownRocket && !contract.AllowHistoric)
            {
                Result($"Миссия выполнена: {contract.Title}", "Контракт засчитывается только своей ракетой из конструктора.");
                return;
            }
            reward = state.Complete(contract, u.Time);
            CareerStore.Save();
            if (reward == null) return;
            Result($"КОНТРАКТ ВЫПОЛНЕН: {contract.Title}",
                $"+{reward.Funds:F0} M₢   +{reward.Science:F0} S   +{reward.Reputation:F0} P" +
                (reward.RaceNote != null ? $"\nГонка: {reward.RaceNote} (престиж ×{reward.RaceFactor:F2}, {contract.RivalName})" : "") +
                $"\n\nКазна {CareerStore.Money(state.Funds)} · наука {state.Science:F0} S · престиж {state.Reputation:F0} P");
        }

        void Result(string title, string body)
        {
            resultText = title + "\n" + body;
            resultOpen = true;
            Say(title);
        }

        void OnDestroy()
        {
            if (gate != Gate.None) Time.timeScale = 1;
            // Дата карьеры — по концу полёта (варп к Нептуну двигает календарь и бюджеты).
            if (counted && state == Career.Current && GameBootstrap.U != null)
            {
                state.AdvanceDate(GameBootstrap.U.Time);
                CareerStore.Save();
            }
        }

        void OnApplicationQuit() { if (counted) CareerStore.Save(); }

        // ---------------------------------------------------------------- IMGUI

        void OnGUI()
        {
            if (state == null || PauseMenu.IsOpen) return;
            if (gate == Gate.None && !resultOpen) return;
            Styles();
            GUI.depth = -10;
            if (gate != Gate.None) DrawGate();
            else DrawResult();
        }

        void DrawGate()
        {
            string body = gate == Gate.Locked
                ? $"В ракете «{priceWhat}» есть неизученные детали:\n{Names(locked)}\n\nИзучите узлы в окне карьеры (Esc → Карьера…) или замените детали."
                : $"Пуск «{priceWhat}» стоит {CareerStore.Money(price)}, в казне {CareerStore.Money(state.Funds)}.\n" +
                  $"Бюджет придёт 1 января {state.Year + 1} г.: ≈ {CareerStore.Money(state.Budget(state.Year + 1))}.";
            Panel("КАРЬЕРА: ПУСК НЕ ЗАСЧИТАН", body, 3, out var r, out float y);
            float w = r.width - 40, x = r.x + 20;
            if (GUI.Button(new Rect(x, y, w, 34), "В конструктор", btn))
            {
                HangarController.ReturnMissionId = GameBootstrap.Instance?.Mission?.Id;
                Time.timeScale = 1;
                SceneManager.LoadScene(HangarController.SceneName);
            }
            y += 40;
            if (gate == Gate.Funds && GUI.Button(new Rect(x, y, w, 34), "Ждать бюджета до 1 января и лететь", btn))
            {
                state.WaitNextYear();
                if (state.Funds >= price) Pay();
                CareerStore.Save();
            }
            if (gate == Gate.Funds) y += 40;
            if (GUI.Button(new Rect(x, y, w, 34), "Лететь вне карьеры (без оплаты и наград)", btn))
            {
                gate = Gate.None;
                counted = false;
                Time.timeScale = 1;
                Say("Полёт вне карьеры: наука и контракты не засчитываются");
            }
        }

        void DrawResult()
        {
            Panel("КАРЬЕРА", resultText, 1, out var r, out float y);
            if (GUI.Button(new Rect(r.x + 20, y, r.width - 40, 34), "Продолжить", btn)) resultOpen = false;
        }

        float Panel(string title, string body, int buttons, out Rect r, out float y)
        {
            float tw = PopupW - 40;
            float th = text.CalcHeight(new GUIContent(body), tw);
            float h = 20 + 30 + 8 + th + 16 + buttons * 40 + 10;
            r = new Rect((Screen.width - PopupW) / 2, (Screen.height - h) / 2, PopupW, h);
            GUI.color = new Color(0.06f, 0.08f, 0.12f, 0.96f);
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
            y = r.y + 16;
            GUI.Label(new Rect(r.x + 20, y, tw, 30), title, head); y += 38;
            GUI.Label(new Rect(r.x + 20, y, tw, th), body, text); y += th + 16;
            return h;
        }

        static string Names(List<string> ids)
        {
            var parts = new List<string>();
            foreach (var id in ids)
            {
                var node = CareerTech.NodeFor(id);
                parts.Add($"• {PartCatalog.Get(id)?.Name ?? id}" + (node != null ? $" — узел «{node.Title}»" : ""));
            }
            return string.Join("\n", parts);
        }

        void Styles()
        {
            if (head != null) return;
            head = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter, wordWrap = true };
            head.normal.textColor = new Color(1f, 0.85f, 0.45f);
            text = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            text.normal.textColor = Color.white;
            btn = new GUIStyle(GUI.skin.button) { fontSize = 16 };
        }
    }
}
