using System;
using System.Collections.Generic;
using System.Reflection;
using APIShared.Internal;
using APIShared.Localization;
using APIShared.Presentation;
using CrusaderDE;
using Noesis;
using SHCDESE.API;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APIShared.Economy
{
    internal static partial class WeaponProductionHud
    {
        private sealed class PanelView
        {
            private static readonly SolidColorBrush ChosenBrush = new SolidColorBrush(Color.FromArgb(255, 248, 207, 118));
            private readonly Grid root, slotHost;
            private readonly TextBlock currentLabel, nextLabel;
            private readonly Slot[] slots = new Slot[4];
            private readonly List<(FrameworkElement element, Visibility previous)> hidden = new List<(FrameworkElement, Visibility)>(3);
            private IReadOnlyList<WeaponProductionOption> displayed;
            private WeaponProductionOption current, next, pressedOption;
            private Target target, pressedTarget;
            private HUD_Main rolloverHud;
            private string rolloverText;
            private bool showing, sendFailureLogged;

            internal PanelView(HUD_Buildings hud)
            {
                Hud = hud;
                root = Require<Grid>("APISharedWeaponProductionPanel");
                currentLabel = Require<TextBlock>("APISharedWeaponProductionCurrent");
                nextLabel = Require<TextBlock>("APISharedWeaponProductionNext");
                slotHost = Require<Grid>("APISharedWeaponProductionSlots");
                if (!(root.TryFindResource("APISharedWeaponProductionButton") is Style style))
                    throw new InvalidOperationException("Weapon production button style is missing.");
                for (int i = 0; i < slots.Length; ++i)
                {
                    var frame = new Grid { HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
                    var icon = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
                    var button = new Button { Style = style, Content = icon };
                    var chosen = new Border { BorderBrush = ChosenBrush, BorderThickness = new Thickness(2),
                        CornerRadius = new CornerRadius(3), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
                    frame.Children.Add(button);
                    frame.Children.Add(chosen);
                    slotHost.Children.Add(frame);
                    slots[i] = new Slot(frame, button, icon, chosen);
                    button.PreviewMouseDown += OnPress;
                    button.Click += OnClick;
                    button.MouseEnter += OnEnter;
                    button.MouseLeave += OnLeave;
                }
            }

            internal HUD_Buildings Hud { get; }
            internal bool IsShowing => showing;

            private T Require<T>(string name) where T : class => Hud.FindName(name) as T ??
                throw new InvalidOperationException("Weapon production HUD member is missing: " + name);

            internal void Show(HUD_Main mainHud, Target value, IReadOnlyList<WeaponProductionOption> options, int allowed,
                WeaponProductionOption selectedCurrent, WeaponProductionOption selectedNext)
            {
                bool sameTarget = IsShowing && target.Matches(value);
                if (!sameTarget)
                {
                    Hide();
                    SuppressVanilla(value.Type);
                }
                if (!ReferenceEquals(rolloverHud, mainHud)) ClearRollover();
                rolloverHud = mainHud;
                if (!sameTarget || !ReferenceEquals(displayed, options)) Arrange(options);
                for (int i = 0; i < options.Count; ++i)
                {
                    bool enabled = (allowed & (1 << i)) != 0;
                    if (slots[i].Button.IsEnabled != enabled) slots[i].Button.IsEnabled = enabled;
                }
                if (!sameTarget || !ReferenceEquals(current, selectedCurrent) || !ReferenceEquals(next, selectedNext) ||
                    target.LackingResources != value.LackingResources)
                {
                    current = selectedCurrent;
                    next = selectedNext;
                    currentLabel.Text = CurrentLabel(value, current);
                    nextLabel.Text = WorkshopText(4) + " " + NameOf(next);
                    foreach (Slot slot in slots)
                        SetVisibility(slot.Chosen, slot.Option != null && next != null && slot.Option.Id.Equals(next.Id));
                }
                target = value;
                displayed = options;
                SetVisibility(root, true);
                showing = true;
            }

            private static string CurrentLabel(Target target, WeaponProductionOption option)
            {
                if (target.LackingResources && target.Type == eStructs.STRUCT_BLACKSMITHS_WORKSHOP) return WorkshopText(12);
                if (target.LackingResources && (target.Type == eStructs.STRUCT_FLETCHERS_WORKSHOP ||
                    target.Type == eStructs.STRUCT_POLETURNERS_WORKSHOP)) return WorkshopText(11);
                return WorkshopText(3) + " " + NameOf(option);
            }

            private static string WorkshopText(int index) => Translate.Instance.lookUpText(
                Enums.eTextSections.TEXT_IN_BLACKSMITHS_WORKSHOP, index);

            private static string NameOf(WeaponProductionOption option) => option == null ? "—" : option.IsVanilla
                ? Translate.Instance.lookUpText(Enums.eTextSections.TEXT_GOODS, (int)option.OutputGood) : option.DisplayName;

            private void Arrange(IReadOnlyList<WeaponProductionOption> options)
            {
                ClearRollover();
                bool compact = options.Count > 2;
                int size = compact ? 42 : 57;
                int gap = compact ? 4 : 0;
                int left = compact ? 320 : 380;
                int top = compact ? 100 : 85;
                for (int i = 0; i < slots.Length; ++i)
                {
                    Slot slot = slots[i];
                    slot.Option = i < options.Count ? options[i] : null;
                    if (slot.Option == null) { SetVisibility(slot.Frame, false); continue; }
                    slot.Frame.Width = slot.Frame.Height = size;
                    slot.Frame.Margin = new Thickness(left + (size + gap) * i, top, 0, 0);
                    slot.Icon.Width = slot.Icon.Height = size - 6;
                    slot.Icon.Source = root.TryFindResource(SpriteKey(slot.Option.OutputGood)) as ImageSource ??
                        throw new InvalidOperationException("Weapon icon is missing: " + slot.Option.OutputGood);
                    SetVisibility(slot.Chosen, next != null && slot.Option.Id.Equals(next.Id));
                    SetVisibility(slot.Frame, true);
                }
            }

            private static string SpriteKey(eGoods good)
            {
                // Same ware-to-icon pairing as the Vanilla ReportsWeaponsPanel.
                switch (good)
                {
                    case eGoods.STORED_BOWS: return "UISprites I027";
                    case eGoods.STORED_CROSSBOWS: return "UISprites I029";
                    case eGoods.STORED_SPEARS: return "UISprites I031";
                    case eGoods.STORED_PIKES: return "UISprites I033";
                    case eGoods.STORED_MACES: return "UISprites I035";
                    case eGoods.STORED_SWORDS: return "UISprites I037";
                    case eGoods.STORED_LEATHER_ARMOUR: return "UISprites I039";
                    case eGoods.STORED_METAL_ARMOUR: return "UISprites I041";
                    default: throw new ArgumentOutOfRangeException(nameof(good));
                }
            }

            private void SuppressVanilla(eStructs type)
            {
                string first, second, suffix;
                switch (type)
                {
                    case eStructs.STRUCT_FLETCHERS_WORKSHOP: first = "ProducingBows"; second = "ProducingXBows"; suffix = ""; break;
                    case eStructs.STRUCT_POLETURNERS_WORKSHOP: first = "ProducingSpears"; second = "ProducingPikes"; suffix = "2"; break;
                    case eStructs.STRUCT_BLACKSMITHS_WORKSHOP: first = "ProducingSwords"; second = "ProducingMaces"; suffix = "3"; break;
                    default: return;
                }
                if (!(Require<RadioButton>(first).Parent is StackPanel row) ||
                    !ReferenceEquals(row, Require<RadioButton>(second).Parent))
                    throw new InvalidOperationException("The Vanilla weapon production buttons no longer share their row.");
                var vanillaCurrent = Require<TextBlock>("Producing" + suffix);
                var vanillaNext = Require<TextBlock>(suffix == "3" ? "ProducingNext3" : "PriducingNext" + suffix);
                Suppress(row);
                Suppress(vanillaCurrent);
                Suppress(vanillaNext);
            }

            private void Suppress(FrameworkElement element)
            {
                hidden.Add((element, element.Visibility));
                SetVisibility(element, false);
            }

            internal void Hide()
            {
                if (!showing && hidden.Count == 0 && rolloverHud == null) return;
                showing = false;
                SetVisibility(root, false);
                ClearRollover();
                rolloverHud = null;
                pressedOption = null;
                foreach (var entry in hidden)
                    if (entry.element.Visibility == Visibility.Collapsed) entry.element.Visibility = entry.previous;
                hidden.Clear();
            }

            internal void Unbind()
            {
                Hide();
                foreach (Slot slot in slots)
                {
                    slot.Button.PreviewMouseDown -= OnPress;
                    slot.Button.Click -= OnClick;
                    slot.Button.MouseEnter -= OnEnter;
                    slot.Button.MouseLeave -= OnLeave;
                    slotHost.Children.Remove(slot.Frame);
                }
            }

            private WeaponProductionOption OptionFor(object sender)
            {
                foreach (Slot slot in slots)
                    if (ReferenceEquals(slot.Button, sender)) return slot.Option;
                return null;
            }

            private void OnPress(object sender, MouseButtonEventArgs args)
            {
                pressedOption = null;
                if (args.ChangedButton != MouseButton.Left)
                {
                    if (args.ChangedButton == MouseButton.Right) args.Handled = true;
                    return;
                }
                if (!IsShowing) return;
                pressedOption = OptionFor(sender);
                pressedTarget = target;
            }

            private void OnClick(object sender, RoutedEventArgs args)
            {
                args.Handled = true;
                WeaponProductionOption selected = pressedOption;
                pressedOption = null;
                var buttonOption = OptionFor(sender);
                if (!IsShowing || selected == null || buttonOption == null || !buttonOption.Id.Equals(selected.Id) ||
                    !pressedTarget.Matches(target)) return;
                try { Submit(selected); }
                catch (Exception error) { ReportSendFailure(error.Message); }
            }

            private void Submit(WeaponProductionOption selected)
            {
                var main = MainViewModel.viewModelLoaded ? MainViewModel.Instance : null;
                if (!ReferenceEquals(main?.HUDBuildingPanel, Hud) || !TryReadTarget(main, out Target live) || !live.Matches(pressedTarget)) return;
                var keys = KeyManager.instance;
                if (keys == null) return;
                var result = WeaponProduction.RequestSelection(live.Id, live.GlobalId, selected.Id, keys.isShiftDown());
                if (!result.Submitted) ReportSendFailure(result.Reason);
            }

            private void ReportSendFailure(string reason)
            {
                if (!sendFailureLogged)
                {
                    sendFailureLogged = true;
                    NativeApiLog.Warning(ApiSharedRuntime.ProcessInstance.Logger, "Weapon production selection failed: " + reason);
                }
                SetRollover(UiText.Get("WeaponProduction.SelectionUnavailable"), "");
            }

            private void OnEnter(object sender, MouseEventArgs args)
            {
                var option = OptionFor(sender);
                if (!IsShowing || option == null) return;
                string name = NameOf(option);
                if (option.IngredientAmount is int amount)
                {
                    eGoods ingredient = WeaponProductionCatalog.IngredientGood(option.BuildingType);
                    string label = Translate.Instance.lookUpText(Enums.eTextSections.TEXT_GOODS, (int)ingredient);
                    name += $" ({amount}× {label})";
                }
                SetRollover(name, UiText.Get("WeaponProduction.ShiftHint"));
            }

            private void OnLeave(object sender, MouseEventArgs args) => ClearRollover();

            private void SetRollover(string text, string hint)
            {
                if (rolloverHud == null) return;
                rolloverText = text;
                rolloverHud.SetRolloverOtherString(text, 2, -1, hint);
            }

            private void ClearRollover()
            {
                if (rolloverText != null && ReferenceEquals(rolloverHud?.OtherString, rolloverText))
                    rolloverHud.SetRolloverOtherString("");
                rolloverText = null;
            }

            private static void SetVisibility(FrameworkElement element, bool visible)
            {
                Visibility value = visible ? Visibility.Visible : Visibility.Collapsed;
                if (element.Visibility != value) element.Visibility = value;
            }

            private sealed class Slot
            {
                internal Slot(Grid frame, Button button, Image icon, Border chosen)
                { Frame = frame; Button = button; Icon = icon; Chosen = chosen; }
                internal readonly Grid Frame;
                internal readonly Button Button;
                internal readonly Image Icon;
                internal readonly Border Chosen;
                internal WeaponProductionOption Option;
            }
        }
    }
}
