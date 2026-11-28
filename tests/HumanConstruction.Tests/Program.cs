using System;
using System.Collections.Generic;
using System.IO;
using APIShared;
using APIShared.Buildings;
using APIShared.Savegames;
using SHCDESE.Interop;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
    private static Dictionary<eGoods, int> Amount(eGoods good, int amount) => new Dictionary<eGoods, int> { { good, amount } };
    private static Func<eGoods, int, int> Quote(float multiplier, int percentage = 100) =>
        (_, paid) => HumanConstructionRefundMath.Materials(paid, multiplier, percentage);
    // Actual SE callback arithmetic, including conv.u8. Its result depends on runtime
    // floating-stack precision: .NET and Unity Mono need not truncate .7f identically.
    private static int SeMaterialQuote(int magnitude, float multiplier, int percentage)
    {
        float percentageFactor = percentage / 100f;
        return (int)(ulong)(magnitude * multiplier * percentageFactor);
    }
    private static ConstructionObjectIdentity Id(int slot = 1, uint generation = 1) => new ConstructionObjectIdentity(ConstructionObjectKind.Building, slot, generation);
    private static void Main()
    {
        RefundPrecision(); Ledger(); Transactions(); Console.WriteLine("Human construction: " + checks + " behavior checks passed.");
    }
    private static void RefundPrecision()
    {
        var ledger = new HumanConstructionLedger();
        ledger.Add(Id(), 1, Amount(eGoods.STORED_GOLD, 10));
        Check(ledger.TryQuote(Id(), 1, 1, true, Quote(.7f), out var refund) && refund[eGoods.STORED_GOLD] == SeMaterialQuote(10, .7f, 100),
            "material refund matches SE arithmetic on the executing runtime");
        ledger.Add(Id(2), 1, Amount(eGoods.STORED_GOLD, -10));
        Check(ledger.TryQuote(Id(2), 1, 1, true, Quote(.7f), out refund) && refund[eGoods.STORED_GOLD] == -SeMaterialQuote(10, .7f, 100),
            "signed rebate offset restores sign after the same SE magnitude quote");
        Check(HumanConstructionRefundMath.Materials(100, .29f, 100) == SeMaterialQuote(100, .29f, 100) &&
            HumanConstructionRefundMath.Materials(10, .7f, 50) == SeMaterialQuote(10, .7f, 50), "material percentage boundaries retain SE truncation");
        ledger.Add(Id(3), 1, Amount(eGoods.STORED_GOLD, 100));
        Check(ledger.TryConsume(Id(3), 1, 1, true, (_, paid) => HumanConstructionRefundMath.StationaryUnit(paid, 29), out refund) &&
            refund[eGoods.STORED_GOLD] == 29, "stationary unit percentage uses integer arithmetic before truncation");
        Check(HumanConstructionRefundMath.StationaryUnit(-100, 29) == -29 &&
            HumanConstructionRefundMath.StationaryUnit(-7, 50) == -3, "stationary rebates truncate toward zero");
        Check(HumanConstructionRefundMath.Materials(10, 2f, 100) == 10 &&
            HumanConstructionRefundMath.Materials(-10, 2f, 100) == -10 &&
            HumanConstructionRefundMath.Materials(10, -.5f, 100) == 0 &&
            HumanConstructionRefundMath.Materials(10, float.MaxValue, 0) == 0, "material refunds remain bounded by the signed payment");
        bool rejected = false;
        try { ledger.TryConsume(Id(), 1, 1, true, (_, paid) => paid + 1, out _); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && ledger.Contains(Id()), "an excessive amount quote cannot consume the receipt");
        rejected = false;
        try { ledger.TryConsume(Id(2), 1, 1, true, (_, paid) => -paid, out _); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && ledger.Contains(Id(2)), "a rebate quote cannot reverse its payment sign");
    }
    private static void Ledger()
    {
        var ledger = new HumanConstructionLedger(); var wood = eGoods.STORED_WOOD_PLANKS;
        var paid = Amount(wood, 7); paid[eGoods.STORED_GOLD] = -3; ledger.Add(Id(), 1, paid); paid[wood] = 99;
        Check(!ledger.TryConsume(Id(), 2, 1, true, Quote(.5f), out _), "other actor cannot refund");
        Check(!ledger.TryConsume(Id(), 1, 1, false, Quote(.5f), out _), "AI cannot refund");
        Check(!ledger.TryConsume(Id(1, 2), 1, 1, true, Quote(.5f), out _), "slot reuse cannot refund");
        Check(ledger.TryQuote(Id(), 1, 1, true, Quote(.5f), out var quote) && quote[wood] == 3 && quote[eGoods.STORED_GOLD] == -1, "snapshot, truncation and signed rebate");
        Check(ledger.Count == 1, "preview does not consume");
        Check(ledger.TryConsume(Id(), 1, 1, true, Quote(.5f), out _) && ledger.Count == 0, "consume once");
        Check(!ledger.TryConsume(Id(), 1, 1, true, Quote(.5f), out _), "duplicate callback cannot refund");
        ledger.Add(Id(), 1, Amount(wood, 4));
        Check(!ledger.TryConsume(Id(), 2, 2, true, Quote(1f), out _) && ledger.Count == 0, "ownership transfer retires claim");
        for (int i = 1; i <= 1002; i++) ledger.Add(Id(i, (uint)i), 1, Amount(wood, 5));
        var chunks = ledger.Encode(); Check(chunks.Count == 2, "bounded save chunks");
        var restored = new HumanConstructionLedger(); restored.Restore(chunks);
        Check(restored.Count == 1002 && restored.TryConsume(Id(1002, 1002), 1, 1, true, Quote(.75f), out var refunded) && refunded[wood] == 3, "restore exact last receipt");
        var malformed = (byte[])chunks[0].Clone(); malformed[4] = 255;
        bool rejected = false; try { restored.Restore(new[] { malformed }); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && restored.Count == 1001, "corrupt restore never publishes partial state");
        rejected = false; try { restored.TryConsume(Id(), 1, 1, true, Quote(float.NaN), out _); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && restored.Contains(Id()), "invalid factor leaves claim untouched");
        var reversed = new HumanConstructionLedger(); var goods = new Dictionary<eGoods, int> { { eGoods.STORED_GOLD, 3 }, { wood, 5 } };
        reversed.Add(Id(), 1, goods); var ordered = new HumanConstructionLedger(); ordered.Add(Id(), 1, new Dictionary<eGoods, int> { { wood, 5 }, { eGoods.STORED_GOLD, 3 } });
        Check(Convert.ToBase64String(reversed.Encode()[0]) == Convert.ToBase64String(ordered.Encode()[0]), "save bytes independent of dictionary insertion order");
        var pruning = new HumanConstructionLedger(); pruning.Add(Id(1), 1, Amount(wood, 1)); pruning.Add(Id(2), 2, Amount(wood, 1)); pruning.Add(Id(3), 1, Amount(wood, 1));
        pruning.Prune(identity => identity.Slot == 3 ? (int?)null : 1);
        Check(pruning.Count == 1 && pruning.Contains(Id(1)), "loaded receipts pruned for absent identities or changed owner");
    }
    private static void Transactions()
    {
        Check(!HumanConstructionCosts.TryRegister("third.party", "costs", "payments", out _, out _), "registration fails before runtime adapters");
        HumanConstructionCosts.SetRuntimeAvailability(true, "");
        Check(HumanConstructionCosts.TryRegister("third.party", "costs", "payments", out var registration, out _), "independent mod registration");
        Check(VersionedSaveData.Saves[0].SimulationRelevant, "payment records are simulation relevant");
        var target = new ConstructionCostTarget(1); var wood = eGoods.STORED_WOOD_PLANKS;
        registration.SetCosts(target, Amount(wood, 5)); registration.SetActive(true);
        Check(!HumanConstructionCosts.TryGetEffectiveCosts(target, 1, out _), "quote unavailable before runtime eligibility adapter");
        bool free = false;
        HumanConstructionCosts.SetQuoteEligibility((quotedTarget, player) => player == 1 && !free);
        Check(HumanConstructionCosts.TryGetEffectiveCosts(target, 1, out var effective) && effective[wood] == 5, "human quote shares configured charges");
        Check(!HumanConstructionCosts.TryGetEffectiveCosts(target, 2, out effective) && effective.Count == 0, "AI or other ineligible actor has no Human quote");
        free = true;
        Check(!HumanConstructionCosts.TryGetEffectiveCosts(target, 1, out effective) && effective.Count == 0, "free-first construction suppresses Human quote");
        free = false;
        Check(HumanConstructionCosts.TryBegin(target, 2, false, false, null, _ => 0, out var transaction, out _) && transaction == null, "AI bypass");
        Check(HumanConstructionCosts.TryBegin(target, 1, true, true, null, _ => 0, out transaction, out _) && transaction == null, "free/editor bypass");
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(wood, 10), _ => 14, out _, out var missing) && missing == wood, "same-good base and extra affordability combined");
        Check(HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(wood, 10), _ => 15, out transaction, out _), "exact affordability");
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(wood, 10), _ => 10, out _, out _, transaction), "unreserved amounts do not count as available");
        int booked = 0;
        Check(HumanConstructionCosts.Reserve(transaction, values => { booked += values[wood]; return true; }), "positive extras reserved before native construction");
        Check(HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(wood, 10), _ => 10, out _, out _, transaction), "the active reservation covers its own extras");
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(wood, 10), _ => 9, out _, out missing, transaction) && missing == wood, "a reservation does not cover missing base costs");
        Check(!HumanConstructionCosts.TryBegin(target, 2, true, false, Amount(wood, 10), _ => 10, out _, out _, transaction), "another player's reservation does not count");
        registration.SetCosts(target, Amount(wood, 99));
        Check(HumanConstructionCosts.Commit(transaction, Id(), values => { booked += values[wood]; return true; }) && booked == 5, "transaction freezes charged costs");
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(wood, 10), _ => 108, out _, out _, transaction), "committed amounts no longer count as reserved");
        Check(!HumanConstructionCosts.Commit(transaction, Id(2), _ => true), "commit cannot repeat");
        registration.SetActive(false); registration.ClearCosts();
        Check(HumanConstructionCosts.TryQuoteRefund(Id(), 1, 1, true, Quote(.5f), out var refund) && refund[wood] == 2, "disabled config does not erase paid claim");
        var writer = new VersionedSaveWriter(); VersionedSaveData.Saves[0].Capture(writer);
        HumanConstructionCosts.Retire(Id());
        VersionedSaveData.Saves[0].Loaded(new VersionedSaveLoad { Status = VersionedSaveLoadStatus.Restored, Entries = writer.Entries });
        HumanConstructionCosts.SetIdentityValidator(identity => identity.Equals(Id()) ? (int?)1 : null);
        ApiShared.Lifecycle.Start(new MissionLifecycleNotification());
        Check(HumanConstructionCosts.HasReceipts, "load-before-start retains history");
        Check(HumanConstructionCosts.TryConsumeRefund(Id(), 1, 1, true, Quote(.75f), out refund) && refund[wood] == 3, "restored history uses current refund factor");
        registration.SetCosts(target, Amount(wood, 5)); registration.SetActive(true);
        HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 5, out transaction, out _);
        Check(!HumanConstructionCosts.Reserve(transaction, _ => false) && !HumanConstructionCosts.HasReceipts, "failed atomic reservation creates no refund history");
        HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 5, out transaction, out _);
        int reserved = 0; HumanConstructionCosts.Reserve(transaction, values => { reserved += values[wood]; return true; }); HumanConstructionCosts.Abort(transaction);
        Check(reserved == 0, "failed native construction restores positive reservation");
        Check(!HumanConstructionCosts.Commit(transaction, Id(4), _ => true), "aborted build cannot charge");
        HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 5, out transaction, out _); ApiShared.Lifecycle.End(new MissionLifecycleNotification());
        Check(!HumanConstructionCosts.Commit(transaction, Id(5), _ => true), "old mission transaction cannot charge next mission");
        registration.SetCosts(target, Amount(eGoods.STORED_GOLD, -7));
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(eGoods.STORED_GOLD, 50), _ => 49, out _, out _), "negative-gold-only extras cannot bypass base affordability");
        Check(HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 0, out transaction, out _), "rebate has no positive affordability requirement");
        HumanConstructionCosts.Reserve(transaction, _ => true);
        Check(HumanConstructionCosts.Commit(transaction, Id(6), values => values[eGoods.STORED_GOLD] == -7), "signed gold rebate captured");
        Check(HumanConstructionCosts.TryConsumeRefund(Id(6), 1, 1, true, Quote(.5f), out refund) && refund[eGoods.STORED_GOLD] == -3, "gold rebate returns a negative offset, never another credit");
        VersionedSaveData.Saves[0].Loaded(new VersionedSaveLoad { Status = VersionedSaveLoadStatus.NoRecord });
        Check(!HumanConstructionCosts.HasReceipts, "legacy save never invents paid claims");
        registration.SetCosts(target, new Dictionary<eGoods, int> { { wood, 5 }, { eGoods.STORED_GOLD, -7 } });
        HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 5, out transaction, out _);
        reserved = 0; HumanConstructionCosts.Reserve(transaction, values => { reserved += values[wood]; return true; });
        Check(!HumanConstructionCosts.Commit(transaction, Id(7), _ => false), "failed rebate is reported");
        HumanConstructionCosts.Abort(transaction);
        Check(reserved == 5 && HumanConstructionCosts.TryConsumeRefund(Id(7), 1, 1, true, Quote(1f), out refund) && refund[wood] == 5 && !refund.ContainsKey(eGoods.STORED_GOLD), "failed rebate keeps paid debits and records no ungranted credit");
        registration.SetCosts(target, Amount(eGoods.STORED_GOLD, 7));
        Check(HumanConstructionCosts.TryRegister("another.mod", "costs", "payments-2", out var other, out _), "second independent cost owner");
        other.SetCosts(target, Amount(eGoods.STORED_GOLD, -7)); other.SetActive(true);
        Check(HumanConstructionCosts.TryGetEffectiveCosts(target, 1, out effective) && effective.Count == 0, "opposing owner extras remain eligible without a zero-value display charge");
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(eGoods.STORED_GOLD, 50), _ => 56, out _, out missing) && missing == eGoods.STORED_GOLD, "zero-net quote still checks base plus positive upfront charges");
        Check(HumanConstructionCosts.TryBegin(target, 1, true, false, Amount(eGoods.STORED_GOLD, 50), _ => 57, out transaction, out _), "zero-net quote accepts exact upfront liquidity");
        HumanConstructionCosts.Abort(transaction);
        other.SetCosts(target, new Dictionary<eGoods, int> { { eGoods.STORED_GOLD, -2 }, { wood, 3 } });
        Check(HumanConstructionCosts.TryGetEffectiveCosts(target, 1, out effective) && effective[eGoods.STORED_GOLD] == 5 && effective[wood] == 3, "effective quote aggregates independent owners by good");
        other.SetCosts(target, Amount(eGoods.STORED_GOLD, -7));
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 0, out _, out _), "one owner's later rebate cannot finance another owner's pre-reservation");
        HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 7, out transaction, out _);
        int goldBooked = 0;
        HumanConstructionCosts.Reserve(transaction, values => { goldBooked += values[eGoods.STORED_GOLD]; return true; });
        Check(goldBooked == 7, "positive gold remains reserved despite combined zero extras");
        HumanConstructionCosts.Commit(transaction, Id(8), values => { goldBooked += values[eGoods.STORED_GOLD]; return true; });
        Check(goldBooked == 0, "post-success gold rebate offsets reserved charge");
        Check(HumanConstructionCosts.TryConsumeRefund(Id(8), 1, 1, true, Quote(1f), out refund) && refund[eGoods.STORED_GOLD] == 0 && !HumanConstructionCosts.HasReceipts, "both owner receipts consumed together without duplicate payout");
        VersionedSaveData.Saves[0].Loaded(new VersionedSaveLoad { Status = VersionedSaveLoadStatus.TooNew });
        Check(!HumanConstructionCosts.TryGetEffectiveCosts(target, 1, out effective), "unwritable receipt owner blocks effective quote rather than silently omitting its charge");
        Check(!HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 7, out _, out _), "kept newer archive cannot accept unpersistable new payment history");
        ApiShared.Lifecycle.End(new MissionLifecycleNotification()); ApiShared.Lifecycle.Start(new MissionLifecycleNotification());
        Check(HumanConstructionCosts.TryBegin(target, 1, true, false, null, _ => 7, out transaction, out _), "fresh mission restores payment-history writability");
        HumanConstructionCosts.Abort(transaction);
    }
}
