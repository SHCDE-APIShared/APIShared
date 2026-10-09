using APIShared.GameModes;
using BepInEx.Logging;
using CrusaderDE;
using MonoMod.RuntimeDetour;
using Noesis;
using R3;
using SHCDESE.API;
using SHCDESE.EventAPI;
using SHCDESE.EventAPI.MapLoader;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace APIShared
{
    internal sealed unsafe partial class UnitHudPresentationService
    {
        private bool RegisterCategory(string owner, UnitHudCategoryDefinition definition, UnitHudCategoryMatcher matcher, out NativeCapabilityDiagnostic diagnostic)
        {
            if (definition == null || matcher == null || string.IsNullOrWhiteSpace(definition.CategoryId) || string.IsNullOrWhiteSpace(definition.DisplayName) ||
                definition.BaseUnitType < 0 || definition.Surfaces == UnitHudSurface.None || (definition.Surfaces & ~UnitHudSurface.All) != 0)
                return Fail("Category definition, matcher, IDs, base type and surfaces are required.", out diagnostic);
            lock (sync)
            {
                if (categories.Any(x => x.Owner == owner && x.Definition.CategoryId == definition.CategoryId))
                    return Fail("The owner already registered this category ID.", out diagnostic);
                categories.Add(new CategoryRegistration(owner, definition, matcher));
                SortRegistrations();
                RebuildActiveViews();
            }
            diagnostic = Available("Category registered for the process lifetime.");
            return true;
        }

        private bool RegisterInteraction(string owner, string id, UnitHudInteractionHandler handler, out NativeCapabilityDiagnostic diagnostic)
        {
            if (string.IsNullOrWhiteSpace(id) || handler == null)
                return Fail("Interaction ID and handler are required.", out diagnostic);
            lock (sync)
            {
                if (interactions.Any(x => x.Owner == owner && x.Id == id))
                    return Fail("The owner already registered this interaction ID.", out diagnostic);
                interactions.Add(new InteractionRegistration(owner, id, handler));
                interactions.Sort((a, b) => Compare(a.Owner, a.Id, b.Owner, b.Id));
                RebuildActiveViews();
            }
            diagnostic = Available("Interaction observer registered for the process lifetime.");
            return true;
        }

        private bool RegisterImage(string owner, UnitHudImageOverrideDefinition definition, UnitHudImageOverrideResolver resolver, out NativeCapabilityDiagnostic diagnostic)
        {
            if (definition == null || resolver == null || string.IsNullOrWhiteSpace(definition.OverrideId) || !Enum.IsDefined(typeof(UnitHudImageSlot), definition.Slot))
                return Fail("A valid image override definition and resolver are required.", out diagnostic);
            lock (sync)
            {
                if (imageOverrides.Any(x => x.Owner == owner && x.Definition.OverrideId == definition.OverrideId))
                    return Fail("The owner already registered this image override ID.", out diagnostic);
                imageOverrides.Add(new ImageRegistration(owner, definition, resolver));
                imageOverrides.Sort((a, b) =>
                {
                    int result = a.Definition.Priority.CompareTo(b.Definition.Priority);
                    return result != 0 ? result : Compare(a.Owner, a.Definition.OverrideId, b.Owner, b.Definition.OverrideId);
                });
                RebuildActiveViews();
            }
            diagnostic = Available("Image override registered for the process lifetime.");
            return true;
        }

        private bool RegisterRecruitment(string owner, string categoryId, UnitHudRecruitmentHandler handler, out NativeCapabilityDiagnostic diagnostic)
        {
            if (string.IsNullOrWhiteSpace(categoryId) || handler == null)
                return Fail("Recruitment category ID and handler are required.", out diagnostic);
            lock (sync)
            {
                CategoryRegistration category = categories.FirstOrDefault(x => x.Owner == owner && x.Definition.CategoryId == categoryId);
                if (category == null || !HasSurface(category, UnitHudSurface.Recruitment))
                    return Fail("Recruitment requires an existing owner-local category with the Recruitment surface.", out diagnostic);
                if (category.Definition.BaseUnitType != (int)eChimps.CHIMP_TYPE_ARCHER)
                    return Fail("The current UI implementation supports recruitment variants only for the European Archer.", out diagnostic);
                if (recruitment.Any(x => x.Category.Key == category.Key))
                    return Fail("The owner already registered recruitment for this category.", out diagnostic);
                recruitment.Add(new RecruitmentRegistration(category, handler));
                recruitment.Sort((a, b) =>
                {
                    int result = a.Category.Definition.Order.CompareTo(b.Category.Definition.Order);
                    return result != 0 ? result : StringComparer.Ordinal.Compare(a.Category.Key, b.Category.Key);
                });
                RebuildActiveViews();
            }
            diagnostic = Available("Recruitment variant registered for the process lifetime.");
            return true;
        }

        private bool CompleteRecruitment(string owner, UnitHudRecruitmentTicket ticket, int matchedCount, string reason, out NativeCapabilityDiagnostic diagnostic)
        {
            if (ticket == null || ticket.TicketId <= 0 || matchedCount < 0 || ticket.OwnerGuid != owner)
                return Fail("A valid owner-bound recruitment ticket and non-negative matched count are required.", out diagnostic);
            lock (sync)
            {
                if (recruitmentLease == null || recruitmentLease.Ticket.TicketId != ticket.TicketId || recruitmentLease.Ticket.OwnerGuid != owner)
                    return Fail("The recruitment ticket is no longer active.", out diagnostic);
                recruitmentLease = null;
                refreshRequested = true;
            }
            diagnostic = Available($"Recruitment completed with {matchedCount} matched units: {reason ?? string.Empty}");
            return true;
        }

        // Call under sync. Activation is pushed by owners, never polled through callbacks.
        private bool OwnerActive(string owner) => !ownerActivation.TryGetValue(owner, out bool active) || active;

        private void RebuildActiveViews()
        {
            restoreSurfaces |= activeSurfaces;
            restoreImages |= activeImages;
            categoryView = categories.Where(x => x.Active && OwnerActive(x.Owner)).ToArray();
            interactionView = interactions.Where(x => OwnerActive(x.Owner)).ToArray();
            imageView = imageOverrides.Where(x => x.Active && OwnerActive(x.Owner)).ToArray();
            recruitmentViews.Clear();
            foreach (var group in recruitment.Where(x => x.Category.Active && OwnerActive(x.Category.Owner)).GroupBy(x => x.Category.Definition.BaseUnitType))
                recruitmentViews[group.Key] = group.ToArray();
            activeRecruitmentHandlers = recruitmentViews.Count != 0;
            UnitHudSurface surfaces = UnitHudSurface.None;
            foreach (CategoryRegistration category in categoryView) surfaces |= category.Definition.Surfaces;
            if (!activeRecruitmentHandlers) surfaces &= ~UnitHudSurface.Recruitment;
            activeSurfaces = surfaces;
            activeImages = imageView.Length != 0;
            refreshRequested = surfaces != UnitHudSurface.None || activeImages || restoreSurfaces != UnitHudSurface.None || restoreImages;
            pendingPresentation = refreshRequested;
        }

        private void SetOwnerActive(string owner, bool active)
        {
            lock (sync)
            {
                bool previous = OwnerActive(owner);
                ownerActivation[owner] = active;
                if (previous != active) RebuildActiveViews();
            }
        }

        private bool SetRegistrationActive(string owner, string id, bool active, bool image)
        {
            lock (sync)
            {
                if (image)
                {
                    ImageRegistration item = imageOverrides.FirstOrDefault(x => x.Owner == owner && x.Definition.OverrideId == id);
                    if (item == null) return false;
                    if (item.Active == active) return true;
                    item.Active = active;
                }
                else
                {
                    CategoryRegistration item = categories.FirstOrDefault(x => x.Owner == owner && x.Definition.CategoryId == id);
                    if (item == null) return false;
                    if (item.Active == active) return true;
                    item.Active = active;
                }
                RebuildActiveViews();
                return true;
            }
        }

        private void RequestRefresh(string owner)
        {
            lock (sync)
            {
                if (!OwnerActive(owner) || (!categoryView.Any(x => x.Owner == owner) && !imageView.Any(x => x.Owner == owner) &&
                    !actionButtons.Any(x => x.Owner == owner && x.Visible))) return;
                foreach (var item in actionButtons.Where(x => x.Owner == owner)) item.Version++;
                refreshRequested = true;
                pendingPresentation = true;
            }
        }

        private void SortRegistrations() => categories.Sort((a, b) =>
        {
            int result = a.Definition.BaseUnitType.CompareTo(b.Definition.BaseUnitType);
            if (result == 0) result = a.Definition.Order.CompareTo(b.Definition.Order);
            return result != 0 ? result : Compare(a.Owner, a.Definition.CategoryId, b.Owner, b.Definition.CategoryId);
        });

        private static int Compare(string ownerA, string idA, string ownerB, string idB)
        {
            int result = StringComparer.Ordinal.Compare(ownerA, ownerB);
            return result != 0 ? result : StringComparer.Ordinal.Compare(idA, idB);
        }

        private bool Fail(string reason, out NativeCapabilityDiagnostic diagnostic)
        {
            diagnostic = new NativeCapabilityDiagnostic(NativeCapabilityIds.UnitHudPresentation, NativeCapabilityState.ValidationFailed, ApiSharedRuntime.SupportedHash, reason);
            return false;
        }

        private static NativeCapabilityDiagnostic Available(string reason) =>
            new NativeCapabilityDiagnostic(NativeCapabilityIds.UnitHudPresentation, NativeCapabilityState.Available, ApiSharedRuntime.SupportedHash, reason);

        private sealed class Binding : IUnitHudPresentationCapability, IUnitHudActivationCapability, IUnitHudActionButtonsCapability
        {
            private readonly UnitHudPresentationService service;
            private readonly string owner;
            internal Binding(UnitHudPresentationService service, string owner) { this.service = service; this.owner = owner; }
            public bool TryRegisterActionButton(UnitHudActionButtonDefinition definition,
                out IUnitHudActionButtonRegistration registration, out NativeCapabilityDiagnostic diagnostic) =>
                service.RegisterActionButton(owner, definition, out registration, out diagnostic);
            public bool TryRegisterCategory(UnitHudCategoryDefinition definition, UnitHudCategoryMatcher matcher, out NativeCapabilityDiagnostic diagnostic) => service.RegisterCategory(owner, definition, matcher, out diagnostic);
            public bool TryRegisterInteraction(string registrationId, UnitHudInteractionHandler handler, out NativeCapabilityDiagnostic diagnostic) => service.RegisterInteraction(owner, registrationId, handler, out diagnostic);
            public bool TryRegisterImageOverride(UnitHudImageOverrideDefinition definition, UnitHudImageOverrideResolver resolver, out NativeCapabilityDiagnostic diagnostic) => service.RegisterImage(owner, definition, resolver, out diagnostic);
            public bool TryRegisterRecruitment(string categoryId, UnitHudRecruitmentHandler handler, out NativeCapabilityDiagnostic diagnostic) => service.RegisterRecruitment(owner, categoryId, handler, out diagnostic);
            public bool TryCompleteRecruitment(UnitHudRecruitmentTicket ticket, int matchedCount, string reason, out NativeCapabilityDiagnostic diagnostic) => service.CompleteRecruitment(owner, ticket, matchedCount, reason, out diagnostic);
            public IReadOnlyList<UnitHudSlotSnapshot> GetVisibleTroopSlots() { lock (service.sync) return service.visibleSlots.ToArray(); }
            public IReadOnlyList<UnitHudCategorySnapshot> GetSelectedCategories() => service.CaptureSelectedCategories();
            public IReadOnlyList<UnitHudControlGroupSnapshot> GetControlGroups() => service.CaptureControlGroups();
            public bool TryRemoveUnitFromControlGroups(int unitId, out int removedCount, out NativeCapabilityDiagnostic diagnostic) =>
                service.RemoveUnitFromControlGroups(unitId, out removedCount, out diagnostic);
            public void RequestRefresh() => service.RequestRefresh(owner);
            public void SetOwnerActive(bool active) => service.SetOwnerActive(owner, active);
            public bool SetCategoryActive(string categoryId, bool active) => service.SetRegistrationActive(owner, categoryId, active, false);
            public bool SetImageOverrideActive(string overrideId, bool active) => service.SetRegistrationActive(owner, overrideId, active, true);
        }

        private sealed class CategoryRegistration
        {
            internal bool Active = true;
            internal CategoryRegistration(string owner, UnitHudCategoryDefinition definition, UnitHudCategoryMatcher matcher) { Owner = owner; Definition = definition; Matcher = matcher; Key = owner + ":" + definition.CategoryId; }
            internal string Owner { get; }
            internal UnitHudCategoryDefinition Definition { get; }
            internal UnitHudCategoryMatcher Matcher { get; }
            internal string Key { get; }
        }
        private sealed class InteractionRegistration
        {
            internal InteractionRegistration(string owner, string id, UnitHudInteractionHandler handler) { Owner = owner; Id = id; Handler = handler; }
            internal string Owner { get; } internal string Id { get; } internal UnitHudInteractionHandler Handler { get; }
        }
        private sealed class ImageRegistration
        {
            internal bool Active = true;
            internal ImageRegistration(string owner, UnitHudImageOverrideDefinition definition, UnitHudImageOverrideResolver resolver) { Owner = owner; Definition = definition; Resolver = resolver; }
            internal string Owner { get; } internal UnitHudImageOverrideDefinition Definition { get; } internal UnitHudImageOverrideResolver Resolver { get; }
        }
        private sealed class RecruitmentRegistration
        {
            internal RecruitmentRegistration(CategoryRegistration category, UnitHudRecruitmentHandler handler) { Category = category; Handler = handler; }
            internal CategoryRegistration Category { get; } internal UnitHudRecruitmentHandler Handler { get; }
        }
        private sealed class RecruitmentLease
        {
            internal RecruitmentLease(UnitHudRecruitmentTicket ticket, float expiresAt) { Ticket = ticket; ExpiresAt = expiresAt; }
            internal UnitHudRecruitmentTicket Ticket { get; } internal float ExpiresAt { get; }
        }
    }
}
