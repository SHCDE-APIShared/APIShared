using System;
using System.Collections.Generic;
using APIShared.Events;
using CrusaderDE;
using Noesis;
using SHCDESE.NoesisUtil;

namespace APIShared.Presentation
{
    /// <summary>One current action presentation. Providers are called on the Unity thread while the menu is visible.</summary>
    public sealed class IngameMenuActionState
    {
        /// <summary>Creates a presentation; hidden entries occupy no space.</summary>
        public IngameMenuActionState(string text, bool visible, bool enabled)
        { Text = text ?? string.Empty; Visible = visible; Enabled = enabled; }
        /// <summary>Localized button label.</summary>
        public string Text { get; }
        /// <summary>Whether this action participates in layout.</summary>
        public bool Visible { get; }
        /// <summary>Whether the visible action can execute.</summary>
        public bool Enabled { get; }
    }

    /// <summary>Additive pause-menu actions next to Vanilla Quit. Register on Unity during startup.
    /// State and commands run on Unity; providers are isolated and ordered by order/owner/ID.
    /// Original Quit identity, command, confirmation and pause behavior remain owned by Vanilla.
    /// Registrations are permanent; hide an action through its provider. No mod-presence decisions.</summary>
    public static class IngameMenuActions
    {
        private sealed class Entry
        {
            internal string Owner, Id; internal int Order, Failures;
            internal Func<IngameMenuActionState> State; internal Action Execute;
            internal Button Button; internal string LastText; internal bool? LastEnabled;
        }
        private delegate void Init(HUD_IngameMenu menu);
        private static readonly ManagedInterceptionHook<Init> hook = new ManagedInterceptionHook<Init>();
        private static readonly List<Entry> entries = new List<Entry>();
        private static HUD_IngameMenu current;
        private static Grid host;
        private static Button quit;
        private static bool installed;
        private static readonly BepInEx.Logging.ManualLogSource log = BepInEx.Logging.Logger.CreateLogSource("APIShared.Menu");

        /// <summary>Registers one action. Duplicate identities are rejected. The command rechecks current visibility
        /// and enabled state; consumers must additionally validate their gameplay/session prerequisites.</summary>
        public static bool TryRegister(string ownerGuid, string id, Func<IngameMenuActionState> state,
            Action execute, out string reason, int order = 0)
        {
            reason = null;
            try
            {
                if (string.IsNullOrWhiteSpace(ownerGuid) || string.IsNullOrWhiteSpace(id) || state == null || execute == null)
                    throw new ArgumentException("Menu action requires identity, state and command.");
                foreach (var entry in entries) if (entry.Owner == ownerGuid && entry.Id == id) throw new ArgumentException("Duplicate menu action.");
                if (!installed)
                {
                    hook.Install(typeof(HUD_IngameMenu), nameof(HUD_IngameMenu.Init), typeof(void), Type.EmptyTypes, OnInit, "ingame-menu");
                    if (!PresentationEvents.TryRegister(PresentationOperation.GuiChecks, APISharedPlugin.PluginGuid,
                        "ingame-menu-actions", null, _ => RefreshVisible(), out reason)) return false;
                    installed = true;
                }
                entries.Add(new Entry { Owner = ownerGuid, Id = id, Order = order, State = state, Execute = execute });
                entries.Sort((a,b) => { int n = a.Order.CompareTo(b.Order); if (n == 0) n = StringComparer.Ordinal.Compare(a.Owner,b.Owner); return n != 0 ? n : StringComparer.Ordinal.Compare(a.Id,b.Id); });
                return true;
            }
            catch (Exception error) { reason = error.Message; return false; }
        }
        private static void OnInit(HUD_IngameMenu menu)
        {
            hook.Original(menu);
            try { Resolve(menu); Refresh(); } catch (Exception error) { Report(null, error); }
        }
        private static void RefreshVisible()
        {
            if (!MainViewModel.viewModelLoaded || !MainViewModel.Instance.Show_HUD_IngameMenu) return;
            try { Resolve(MainViewModel.Instance.HUDIngameMenu); Refresh(); } catch (Exception error) { Report(null, error); }
        }
        private static void Resolve(HUD_IngameMenu menu)
        {
            if (menu == null) { current = null; host = null; quit = null; return; }
            var nextHost = menu.FindName("APISharedIngameMenuActions") as Grid;
            var nextQuit = menu.FindName("ButtonQuitMission") as Button;
            if (nextHost == null || nextQuit == null) { current = null; host = null; quit = null; return; } // XAML may still be constructing.
            if (!ReferenceEquals(current, menu) || !ReferenceEquals(host, nextHost))
            {
                current = menu; host = nextHost; quit = nextQuit;
                foreach (var entry in entries) { entry.Button = null; entry.LastText = null; entry.LastEnabled = null; }
            }
            foreach (var entry in entries)
            {
                if (entry.Button != null) continue;
                var button = new Button { Style = quit.Style, Width = 181.25f, VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
                button.Command = new RelayCommand(() => {
                    try { var state = entry.State(); if (MainViewModel.viewModelLoaded && MainViewModel.Instance.Show_HUD_IngameMenu && ReferenceEquals(MainViewModel.Instance.HUDIngameMenu, menu) && ReferenceEquals(entry.Button, button) && state?.Visible == true && state.Enabled) entry.Execute(); }
                    catch (Exception error) { Report(entry, error); }
                });
                PropEx.SetTextLeft(button, ""); PropEx.SetTextRight(button, "");
                entry.Button = button; host.Children.Add(button);
            }
        }
        private static void Refresh()
        {
            if (host == null || quit == null) return;
            int visible = 0;
            foreach (var entry in entries)
            {
                if (entry.Button == null) continue;
                IngameMenuActionState state = null;
                try { state = entry.State(); } catch (Exception error) { Report(entry, error); }
                var button = entry.Button;
                bool show = state?.Visible == true;
                button.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (!show) continue;
                if (entry.LastText != state.Text) { PropEx.SetTextCentre(button, state.Text); entry.LastText = state.Text; }
                if (entry.LastEnabled != state.Enabled) { button.IsEnabled = state.Enabled; entry.LastEnabled = state.Enabled; }
                // Each additional action receives its own row; the first shares the original Quit row.
                button.Margin = new Thickness(0, visible++ * 70, 0, 0);
            }
            quit.Width = visible == 0 ? 300f : 181.25f;
            quit.HorizontalAlignment = visible == 0 ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        }
        private static int hostFailures;
        private static void Report(Entry entry, Exception error)
        {
            if (entry == null ? ++hostFailures != 1 : ++entry.Failures != 1) return;
            try { log.LogError("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] Ingame menu action failed: " + (entry?.Owner ?? "layout") + ": " + error); } catch { }
        }
    }
}
