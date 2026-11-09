using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Globalization;
using System.Text;
using APIShared.GameModes;
using APIShared.Modifiers;

namespace APIShared.Economy
{
    // A bundle is a finite arithmetic commitment, never evidence that any hypothetical mode
    // actually launched. The real lifecycle must select its exact member before activation.
    internal sealed class WorkerProductionLaunchBundle
    {
        internal WorkerProductionLaunchBundle(IEnumerable<KeyValuePair<string,WorkerProductionCompiledRules>> members,
            IList<WorkerProductionIssue> issues,bool hasBase,bool active,bool desired,bool available,string key,int preview)
        {
            var sorted=new SortedDictionary<string,WorkerProductionCompiledRules>(StringComparer.Ordinal);
            foreach(var member in members)
            {
                sorted.Add(member.Key,member.Value);
                NeedsFullBackend |= member.Value.NeedsFullBackend;
                RequiresExtendedWire |= member.Value.RequiresExtendedWire;
            }
            Members=new ReadOnlyDictionary<string,WorkerProductionCompiledRules>(sorted);
            Issues=Array.AsReadOnly(new List<WorkerProductionIssue>(issues).ToArray());
            HasBaseContributions=hasBase; HasActiveContributions=active; HasDesiredProfiles=desired;
            IsAvailable=available; ServiceKey=key ?? string.Empty; ResolvedFixedBonusPercentPreview=preview;
            Digest=Issues.Count==0 ? HashMembers() : string.Empty;
        }
        internal IReadOnlyDictionary<string,WorkerProductionCompiledRules> Members { get; }
        internal IReadOnlyList<WorkerProductionIssue> Issues { get; }
        internal bool CanStart => Issues.Count==0;
        internal bool IsAvailable { get; }
        internal bool HasBaseContributions { get; }
        internal bool NeedsFullBackend { get; }
        internal bool RequiresExtendedWire { get; }
        internal bool HasActiveContributions { get; }
        internal bool HasDesiredProfiles { get; }
        internal int ResolvedFixedBonusPercentPreview { get; }
        internal string Digest { get; }
        internal string ServiceKey { get; }
        internal bool Contains(GameModeSnapshot actual,WorkerProductionCompiledRules candidate) => candidate!=null && CanStart &&
            !actual.HasConflictingCustomizedOrigin && Members.TryGetValue(ModeKey(actual),out var member) && member.Digest==candidate.Digest;
        internal static string ModeKey(GameModeSnapshot mode) => ((int)mode.Kind).ToString(CultureInfo.InvariantCulture)+"/"+
            ((int)mode.LaunchVariant).ToString(CultureInfo.InvariantCulture)+"/"+(mode.IsRealMultiplayer ? "1":"0");
        private string HashMembers()
        {
            using(var data=new MemoryStream()) using(var writer=new BinaryWriter(data))
            {
                writer.Write("APIShared.WorkerProduction.LaunchBundle/1"); writer.Write(Members.Count);
                foreach(var member in Members) { writer.Write(member.Key); writer.Write(member.Value.Digest); }
                writer.Flush(); return Sha256Hex.Of(data.ToArray());
            }
        }
    }

    internal static class WorkerProductionLaunchBundleCompiler
    {
        internal static WorkerProductionLaunchBundle Compile(IReadOnlyList<WorkerProductionProfileContribution> ordered,
            IReadOnlyList<ulong> saved,bool available,Func<bool> fixedAvailable,string key,string diagnostic,WorkerProductionIssue extraIssue=null)
        {
            var members=new SortedDictionary<string,WorkerProductionCompiledRules>(StringComparer.Ordinal);
            var issues=new List<WorkerProductionIssue>();
            if(extraIssue!=null) issues.Add(extraIssue);
            var bySelection=new Dictionary<string,WorkerProductionCompilation>(StringComparer.Ordinal);
            bool hasBase=false,hasActive=false,desired=false,needsFull=false; int preview=50;
            foreach(var item in ordered) desired|=item.Profile!=null;
            foreach(GameModeKind kind in Enum.GetValues(typeof(GameModeKind)))
            foreach(GameModeLaunchVariant variant in Enum.GetValues(typeof(GameModeLaunchVariant)))
            for(int network=0;network<2;network++)
            {
                var mode=Hypothetical(kind,variant,network!=0);
                // Null activation intentionally permits every recognized mode, including Tutorial.
                // A policy's absence of a permission bit denies that owner; it does not erase
                // the mode from the global arithmetic commitment for unrelated owners.
                if(kind==GameModeKind.Unknown) continue;
                // Arithmetic depends only on the selected owners, not on raw loader/roster fields.
                string selection=Selection(ordered,mode);
                if(!bySelection.TryGetValue(selection,out var compilation))
                {
                    compilation=WorkerProductionCompiler.Compile(ordered,mode,saved);
                    bySelection.Add(selection,compilation); issues.AddRange(compilation.Issues);
                }
                hasBase|=compilation.HasBaseContributions; hasActive|=compilation.HasActiveContributions;
                if(kind==GameModeKind.CustomGame && variant==GameModeLaunchVariant.Standard && network==0) preview=compilation.Bonus;
                if(compilation.Rules!=null)
                {
                    members.Add(WorkerProductionLaunchBundle.ModeKey(mode),compilation.Rules);
                    needsFull |= compilation.Rules.NeedsFullBackend;
                }
            }
            needsFull |= hasBase || (saved!=null && saved.Count!=0);
            bool ready=needsFull ? available : fixedAvailable();
            if(!ready && (hasActive || needsFull)) issues.Add(new WorkerProductionIssue(WorkerProductionIssueCode.NativeUnavailable,
                "Worker production protection is unavailable: "+diagnostic));
            return new WorkerProductionLaunchBundle(members,issues,hasBase,hasActive,desired,ready,ready ? key : string.Empty,preview);
        }
        private static string Selection(IReadOnlyList<WorkerProductionProfileContribution> owners,GameModeSnapshot mode)
        {
            var selected=new StringBuilder();
            for(int n=0;n<owners.Count;n++)
            {
                var profile=owners[n].Profile;
                if(profile==null) continue;
                bool active=profile.FixedBonusPercent.Kind!=ValueModifierKind.None || profile.HasFearContributions;
                foreach(var value in profile.BaseAmounts.Values)
                    active|=value.Human.Kind!=ValueModifierKind.None || value.AI.Kind!=ValueModifierKind.None;
                if(!active || !profile.Activation.HasValue || GameplayModModePolicy.IsAllowed(profile.Activation.Value,mode,out _)) selected.Append(n).Append(',');
            }
            return selected.ToString();
        }
        internal static GameModeSnapshot Hypothetical(GameModeKind kind,GameModeLaunchVariant variant,bool multiplayer) =>
            new GameModeSnapshot(isRealMultiplayer:multiplayer,isSingleplayerSkirmishMode:false,isSingleplayerSkirmish:false,
                isSingleplayerTrail:false,isMapEditor:kind==GameModeKind.MapEditor,multiplayerSave:false,directorAvailable:false,
                directorMultiplayer:false,directorSkirmish:false,lowLevelNetworked:false,platformMultiplayer:false,
                lobbyMembers:0,realLobbyMembers:0,skirmishLobbyMembers:0,gameMembers:0,realNetworkGameMembers:0,
                gameType:-1,skirmishGameType:-1,skirmishTrailType:-1,coopTrailId:0,kind:kind,launchVariant:variant,
                campaignMapId:0,eventTrailType:-1,customizedTrailId:0,customizedMissionId:0,customizedOriginKind:0,
                hasConflictingCustomizedOrigin:false);
    }
}
