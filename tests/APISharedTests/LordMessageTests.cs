using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using APIShared.Audio;
using APIShared.Events;
using APIShared.Presentation;
using CrusaderDE;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.Utils;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass]
    [DoNotParallelize]
    public sealed class LordMessageTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;
        private static LordMessageContext Context(long invocation = 1, int header = 0) =>
            new LordMessageContext(invocation, header, (int)Enums.eTextSections.TEXT_SKIRMISH_SPEECH,
                (int)AILordMessageType.RequestGoods, AILords.SK_RAT, null,
                AILordMessageType.RequestGoods, "general_message.wav", "original.bik");

        [TestMethod]
        public void ComponentChoicesAreIndependentAndTheOriginalSelectorsRemainImmutable()
        {
            var mutation = new LordMessageMutation(Context());
            var args = mutation.Public;
            Assert.AreEqual(LordMessageSourceKind.Original, args.Audio.Kind);
            Assert.AreEqual(LordMessageSourceKind.Original, args.Video.Kind);
            Assert.AreEqual(LordMessageSourceKind.Original, args.Text.Kind);
            Assert.IsNull(args.Context.PlayerId, "A Vanilla speech index does not identify a player slot.");
            var originalAudio = args.Audio;
            var originalVideo = args.Video;
            Assert.IsFalse(args.TryReplaceAudio(originalAudio));
            Assert.IsFalse(args.TryReplaceVideo(originalVideo));
            Assert.IsFalse(args.TryReplaceText(args.Text));
            Assert.AreSame(originalAudio, args.Audio); Assert.AreSame(originalVideo, args.Video);
            var text = LordMessageText.FromText("Eigener Text: Grüße! 日本語");
            Assert.IsTrue(args.TryReplaceText(text));
            Assert.AreSame(originalAudio, args.Audio);
            Assert.AreSame(originalVideo, args.Video);
            Assert.AreSame(text, args.Text);
            Assert.AreEqual("Eigener Text: Grüße! 日本語", args.Text.Resolve());
            Assert.IsTrue(args.TryReplaceText(LordMessageText.FromText(string.Empty)), "An explicit empty subtitle is valid.");
            Assert.IsFalse(args.TryReplaceText(null));
            Assert.IsFalse(args.TryReplaceAudio(null));
            Assert.IsFalse(args.TryReplaceVideo(null));
            Assert.AreEqual((int)AILordMessageType.RequestGoods, args.Context.TextIndex);
            Assert.AreEqual("general_message.wav", args.Context.OriginalAudioPath);
            Assert.ThrowsExactly<ArgumentNullException>(() => LordMessageText.FromText(null));
        }

        [TestMethod]
        public void OrderedCallbackRollbackRestoresAllPartsAndOwnerPrivateState()
        {
            var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            var order = new List<string>();
            var successfulAudio = new LordMessageAudio(LordMessageSourceKind.Vanilla, "first.wav");
            var successfulVideo = new LordMessageVideo(LordMessageSourceKind.Vanilla, "last.bik");
            var successfulText = LordMessageText.FromText("first");
            LordMessagePostEventArgs result = null;
            Assert.IsTrue(registry.TryRegister("b", "first", pre => {
                order.Add("first");
                pre.Audio = successfulAudio;
                pre.Text = successfulText;
                pre.State = "private-first";
            }, post => Assert.AreEqual("private-first", post.State), out _));
            Assert.IsTrue(registry.TryRegister("c", "throw", pre => {
                order.Add("throw");
                pre.Audio = new LordMessageAudio(LordMessageSourceKind.Vanilla, "must-rollback.wav");
                pre.Video = new LordMessageVideo(LordMessageSourceKind.Vanilla, "must-rollback.bik");
                pre.Text = LordMessageText.FromText("must rollback");
                pre.State = "must rollback";
                throw new InvalidOperationException("consumer failure");
            }, post => Assert.IsNull(post.State), out _));
            Assert.IsTrue(registry.TryRegister("a", "last-by-order", pre => {
                order.Add("last");
                Assert.AreSame(successfulAudio, pre.Audio);
                Assert.AreSame(successfulText, pre.Text);
                Assert.AreEqual("original.bik", pre.Video.Path);
                Assert.IsNull(pre.State);
                pre.Video = successfulVideo;
                pre.State = "private-last";
            }, post => result = post, out _, order: 1));
            var invocation = registry.Begin(new LordMessageMutation(Context()));
            invocation.Complete(invocation.Pre, (pre, state) => new LordMessagePostEventArgs(pre, state));
            CollectionAssert.AreEqual(new[] { "first", "throw", "last" }, order);
            Assert.AreEqual(1L, registry.CallbackFailures);
            Assert.AreSame(successfulAudio, result.Audio);
            Assert.AreSame(successfulVideo, result.Video);
            Assert.AreSame(successfulText, result.Text);
            Assert.AreEqual("private-last", result.State);
            Assert.ThrowsExactly<InvalidOperationException>(() => invocation.Pre.Public.TryReplaceText(LordMessageText.FromText("late")));
        }

        [TestMethod]
        public void PublicArgsRejectCrossThreadAndPostNotificationMutation()
        {
            var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            LordMessagePreEventArgs retained = null;
            Exception crossThread = null;
            registry.TryRegister("thread", "ownership", pre => {
                retained = pre.Public;
                var thread = new Thread(() => {
                    try { retained.TryReplaceText(LordMessageText.FromText("wrong thread")); }
                    catch (Exception error) { crossThread = error; }
                });
                thread.Start(); thread.Join();
                Assert.IsInstanceOfType<InvalidOperationException>(crossThread);
                Assert.AreEqual(LordMessageSourceKind.Original, retained.Text.Kind);
                Assert.IsTrue(retained.TryReplaceText(LordMessageText.FromText("correct thread")));
            }, null, out _);
            var invocation = registry.Begin(new LordMessageMutation(Context()));
            Assert.AreEqual("correct thread", invocation.Pre.Text.Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => retained.State = "late");
        }

        [TestMethod]
        public void LogicalDisableKeepsInFlightPostPairedAndPreventsSubsequentDemand()
        {
            var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            var handle = new LordMessageRegistration();
            int pres = 0, posts = 0;
            registry.TryRegister("activation", "paired", pre => {
                pres++; pre.State = "captured"; handle.SetEnabled(false);
            }, post => { posts++; Assert.AreEqual("captured", post.State); }, out _, isActive: () => handle.Enabled);
            var first = registry.Begin(new LordMessageMutation(Context()));
            Assert.IsFalse(registry.CanNotify);
            first.Complete(first.Pre, (pre, state) => new LordMessagePostEventArgs(pre, state));
            Assert.AreEqual(1, pres); Assert.AreEqual(1, posts);
            var disabled = registry.Begin(new LordMessageMutation(Context(2)));
            disabled.Complete(disabled.Pre, (pre, state) => new LordMessagePostEventArgs(pre, state));
            Assert.AreEqual(1, pres); Assert.AreEqual(1, posts);
            handle.SetEnabled(true);
            Assert.IsTrue(registry.CanNotify);
        }

        [TestMethod]
        public void PrivateAssetReadinessIsIndependentPerPartAndErrorsPreserveTheCurrentChoice()
        {
            var previous = LordMessageAssets.ResourceAvailable;
            var calls = new List<string>();
            try
            {
                LordMessageAssets.ResourceAvailable = (path, video) => {
                    calls.Add((video ? "video:" : "audio:") + path);
                    if (path.Contains("throw")) throw new InvalidOperationException("resource backend failed");
                    return !path.Contains("missing");
                };
                var mutation = new LordMessageMutation(Context());
                var audio = LordMessageAudio.FromModAsset("unrelated.third.party", "Voices/own.ogg");
                var video = LordMessageVideo.FromModAsset("unrelated.third.party", "Video/own.webm");
                Assert.IsTrue(audio.IsReady); Assert.IsTrue(video.IsReady);
                Assert.AreEqual(2, calls.Count, "Both resource lookups occur during explicit preparation.");
                Assert.IsTrue(mutation.Public.TryReplaceAudio(audio));
                Assert.AreEqual(LordMessageSourceKind.Original, mutation.Video.Kind);
                Assert.IsTrue(mutation.Public.TryReplaceVideo(video));
                Assert.AreSame(audio, mutation.Audio);
                Assert.AreSame(video, mutation.Video);
                Assert.AreEqual(2, calls.Count, "Selecting prepared resources performs no additional index or extraction access.");
                Assert.IsTrue(audio.Path.Contains("_SLA_/unrelated.third.party/"));
                Assert.IsFalse(mutation.Public.TryReplaceAudio(LordMessageAudio.FromModAsset("unrelated.third.party", "Voices/missing.ogg")));
                Assert.IsFalse(mutation.Public.TryReplaceVideo(LordMessageVideo.FromModAsset("unrelated.third.party", "Video/throw.webm")));
                Assert.AreSame(audio, mutation.Audio);
                Assert.AreSame(video, mutation.Video);
                Assert.AreEqual(4, calls.Count);
                foreach (string invalid in new[] { "/absolute.ogg", "C:/absolute.ogg", "../outside.ogg", "a/../outside.ogg", "a//voice.ogg", "a/./voice.ogg", "a\0.ogg" })
                    Assert.ThrowsExactly<ArgumentException>(() => LordMessageAudio.FromModAsset("third.party", invalid), invalid);
                Assert.ThrowsExactly<ArgumentException>(() => LordMessageVideo.FromModAsset("", "video.webm"));
            }
            finally { LordMessageAssets.ResourceAvailable = previous; }
        }

        [TestMethod]
        public void PreparingAssetsInsidePreOrPostIsRejectedBeforeTheResolverAndPreparedSelectionIsIoFree()
        {
            var previous = LordMessageAssets.ResourceAvailable;
            int resolves = 0;
            try
            {
                LordMessageAssets.ResourceAvailable = (path, video) => { resolves++; return !path.Contains("missing"); };
                var audio = LordMessageAudio.FromModAsset("external.owner", "Voices/prepared.ogg");
                var missing = LordMessageVideo.FromModAsset("external.owner", "Video/missing.webm");
                Assert.IsTrue(audio.IsReady); Assert.IsFalse(missing.IsReady); Assert.AreEqual(2, resolves);
                var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
                int preCount = 0, postCount = 0;
                LordMessageEvents.Register("assets", "no-io", pre => {
                    preCount++;
                    Assert.ThrowsExactly<InvalidOperationException>(() => LordMessageAudio.FromModAsset("external.owner", "Voices/forbidden.ogg"));
                    Assert.ThrowsExactly<InvalidOperationException>(() => LordMessageVideo.FromModAsset("external.owner", "Video/forbidden.webm"));
                    Assert.IsTrue(pre.TryReplaceAudio(audio));
                    Assert.IsFalse(pre.TryReplaceVideo(missing));
                    Assert.AreEqual(2, resolves);
                }, post => {
                    postCount++;
                    Assert.ThrowsExactly<InvalidOperationException>(() => LordMessageAudio.FromModAsset("external.owner", "Voices/post-forbidden.ogg"));
                    Assert.AreEqual(2, resolves);
                    Assert.AreSame(audio, post.Audio);
                    Assert.AreEqual(LordMessageSourceKind.Original, post.Video.Kind);
                }, out _, out _, registry: registry);
                var frame = new LordMessageFrame(null, Group(AILords.SK_RAT, AILordMessageType.RequestGoods), 4,
                    new EngineInterface.PlayState(), 1, registry);
                Assert.IsNotNull(frame.Select(0)); frame.Complete();
                Assert.AreEqual(1, preCount); Assert.AreEqual(1, postCount);
                Assert.AreEqual(0L, registry.CallbackFailures, "The consumer catches the expected API rejection itself.");
                Assert.AreEqual(2, resolves);
            }
            finally { LordMessageAssets.ResourceAvailable = previous; }
        }

        [TestMethod]
        public void RealRegistrationAdapterRejectsDuplicatesAndPreservesPrivatePostState()
        {
            var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            var posts = new List<string>();
            Assert.IsTrue(LordMessageEvents.Register("third.party", "one", pre => {
                pre.State = "one";
                Assert.IsTrue(pre.TryReplaceText(LordMessageText.FromText("replacement")));
            }, post => posts.Add((string)post.State), out var one, out _, registry: registry));
            Assert.IsTrue(LordMessageEvents.Register("third.party", "two", pre => {
                Assert.IsNull(pre.State); pre.State = "two";
            }, post => posts.Add((string)post.State), out var two, out _, registry: registry));
            Assert.IsFalse(LordMessageEvents.Register("third.party", "one", pre => Assert.Fail(), null, out _, out _, registry: registry));
            var invocation = registry.Begin(new LordMessageMutation(Context()));
            invocation.Complete(invocation.Pre, LordMessageEvents.PostFactory);
            CollectionAssert.AreEqual(new[] { "one", "two" }, posts);
            Assert.AreEqual("replacement", invocation.Pre.Text.Value);
            one.SetEnabled(false); two.SetEnabled(false);
            Assert.IsFalse(registry.CanNotify);
        }

        [TestMethod]
        public void GroupMatcherUsesExplicitIdentityAndAcceptsOrdinaryNonLoopingVideo()
        {
            var records = Group(AILords.SK_WOLF, AILordMessageType.WillAttackEnemy);
            var before = (short[])records.Clone();
            Assert.IsTrue(LordMessageGroups.TryMatch(records, 4, 0, out var group));
            Assert.AreEqual(AILords.SK_WOLF, group.Lord);
            Assert.AreEqual(AILordMessageType.WillAttackEnemy, group.Type);
            Assert.IsNull(group.Player, "No cached player attribution is permitted for positive text sections.");
            Assert.AreEqual(1, group.Audio); Assert.AreEqual(3, group.Video);
            Assert.AreEqual(0, records[group.Video * 21 + 3], "Zero means a valid ordinary nonlooping video.");
            CollectionAssert.AreEqual(before, records, "Matching must not mark or rewrite native output.");
            for (int player = 1; player <= 8; player++)
            {
                records[11] = (short)-player;
                records[12] = (short)AILordMessageType.RequestGoods;
                Assert.IsTrue(LordMessageGroups.TryMatch(records, 4, 0, out group));
                Assert.AreEqual(player, group.Player);
                Assert.IsNull(group.Lord, "A custom player section does not establish a Vanilla speech lord.");
                Assert.AreEqual(AILordMessageType.RequestGoods, group.Type);
            }
        }

        [TestMethod]
        public void GroupMatcherRejectsIncompleteAnnouncementsCrossedHeadersAndMalformedMedia()
        {
            foreach (var damage in new Action<short[]>[] {
                r => r[0] = 14,
                r => r[11] = 0,
                r => r[11] = -9,
                r => r[12] = 0,
                r => r[12] = 34,
                r => r[13] = 0,
                r => r[14] = 0,
                r => r[13] = 2,
                r => r[21] = 16,
                r => r[21 + 11] = 99,
                r => r[21 + 12] = 3,
                r => r[42] = 20,
                r => r[63] = 18,
                r => r[63 + 3] = 1,
                r => r[63 + 11] = 0 })
            {
                var records = Group(AILords.SK_RAT, AILordMessageType.RequestGoods);
                damage(records);
                Assert.IsFalse(LordMessageGroups.TryMatch(records, 4, 0, out _), "A malformed group must pass through without a callback.");
            }
            var first = Group(AILords.SK_RAT, AILordMessageType.RequestGoods);
            first[42] = 20;
            var combined = first.Concat(Group(AILords.SK_SNAKE, AILordMessageType.ThankGoods)).ToArray();
            Assert.IsFalse(LordMessageGroups.TryMatch(combined, 8, 0, out _), "The first group cannot borrow a later header's video.");
            Assert.IsTrue(LordMessageGroups.TryMatch(combined, 8, 4, out var second));
            Assert.AreEqual(AILords.SK_SNAKE, second.Lord);
            Assert.IsFalse(LordMessageGroups.TryMatch(null, 0, 0, out _));
            Assert.IsFalse(LordMessageGroups.TryMatch(combined, 9, 0, out _));
            Assert.IsFalse(LordMessageGroups.TryMatch(combined, 8, -1, out _));
            Assert.IsFalse(LordMessageGroups.TryMatch(combined, 8, 8, out _));
            Assert.IsFalse(LordMessageGroups.TryMatch(combined, 3, 0, out _));
        }

        [TestMethod]
        public void OptionalTransitionControlsStayInsideTheirOwnValidatedGroup()
        {
            var source = Group(AILords.SK_PIG, AILordMessageType.TeamWinning);
            var records = new short[7 * 21];
            Array.Copy(source, 0, records, 0, 3 * 21);
            records[3 * 21] = 19;
            records[4 * 21] = 17;
            records[5 * 21] = 21; records[5 * 21 + 3] = -1;
            Array.Copy(source, 3 * 21, records, 6 * 21, 21);
            Assert.IsTrue(LordMessageGroups.TryMatch(records, 7, 0, out var group));
            Assert.AreEqual(6, group.Video);
            records[4 * 21] = 14;
            Assert.IsFalse(LordMessageGroups.TryMatch(records, 7, 0, out _), "An unrelated UI record is not an audited transition.");
        }

        [TestMethod]
        public void FrameKeepsSameLordMessagesSeparateAndCompletesOnlyTheirActualVideo()
        {
            var records = Group(AILords.SK_RAT, AILordMessageType.RequestGoods)
                .Concat(Group(AILords.SK_RAT, AILordMessageType.RequestGoods)).ToArray();
            var before = (short[])records.Clone();
            var contexts = new List<LordMessageContext>();
            var posts = new List<LordMessagePostEventArgs>();
            var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            LordMessageEvents.Register("frame", "identity", pre => {
                contexts.Add(pre.Context);
                pre.State = pre.Context.HeaderIndex;
                pre.TryReplaceText(LordMessageText.FromText("message " + pre.Context.HeaderIndex));
            }, post => posts.Add(post), out _, out _, registry: registry);
            var state = new EngineInterface.PlayState { speechFileName = "effective.wav", binkFileName = "effective.bik" };
            var frame = new LordMessageFrame(null, records, 8, state, 73, registry);
            var first = frame.Select(0);
            Assert.AreSame(first, frame.AtAudio(1)); Assert.IsNull(frame.AtAudio(5));
            Assert.AreSame(first, frame.AtVideo(3)); Assert.IsNull(frame.AtVideo(7));
            frame.Complete(); frame.Complete();
            Assert.AreEqual(1, posts.Count, "Repeated completion cannot replay Post.");
            Assert.IsNull(frame.AtAudio(1));
            var second = frame.Select(4);
            Assert.AreNotSame(first, second);
            Assert.AreEqual("message 4", second.Text.Value);
            Assert.AreSame(second, frame.AtAudio(5)); Assert.IsNull(frame.AtAudio(1));
            frame.Complete();
            Assert.AreEqual(2, posts.Count);
            Assert.AreEqual(0, posts[0].State); Assert.AreEqual(4, posts[1].State);
            Assert.AreEqual(73L, contexts[0].InvocationId); Assert.AreEqual(73L, contexts[1].InvocationId);
            Assert.AreNotEqual(contexts[0].HeaderIndex, contexts[1].HeaderIndex);
            Assert.AreEqual("effective.wav", contexts[0].OriginalAudioPath);
            CollectionAssert.AreEqual(before, records);
        }

        [TestMethod]
        public void FrameActivationSnapshotSurvivesDisableButAbandonedOrInvalidGroupsHaveNoPost()
        {
            var records = Group(AILords.SK_SNAKE, AILordMessageType.RequestGoods);
            var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            int pres = 0, posts = 0;
            LordMessageRegistration handle = null;
            LordMessageEvents.Register("frame", "activation", pre => { pres++; handle.SetEnabled(false); },
                post => posts++, out handle, out _, registry: registry);
            var frame = new LordMessageFrame(null, records, 4, new EngineInterface.PlayState(), 1, registry);
            var selected = frame.Select(0);
            Assert.IsNotNull(selected); Assert.AreSame(selected, frame.AtVideo(3));
            frame.Complete(); Assert.AreEqual(1, posts);
            Assert.IsNull(frame.Select(0)); Assert.AreEqual(1, pres);
            handle.SetEnabled(true);
            Assert.IsNotNull(frame.Select(0)); frame.Abandon(); frame.Complete();
            Assert.AreEqual(1, posts, "A media path that never completes has no success Post.");
            handle.SetEnabled(true); records[21 + 12] = 2;
            Assert.IsNull(frame.Select(0)); Assert.AreEqual(2, pres);
        }

        [TestMethod]
        public void TextBindingRetiresIdenticalRepeatedSelectorsAndDoesNotCrossOstInstances()
        {
            var bindings = new LordMessageTextBindings();
            var slot = new OnScreenText.OST { data1 = 221, data2 = 14 };
            var first = LordMessageText.FromText("first");
            bindings.Bind(slot, first);
            Assert.IsTrue(bindings.TryGet(slot, out var text)); Assert.AreSame(first, text);
            Assert.IsFalse(bindings.TryGet(new OnScreenText.OST { data1 = 221, data2 = 14 }, out _));
            bindings.Clear();
            Assert.IsFalse(bindings.TryGet(slot, out _), "Every new Add must retire even the same slot and selectors.");
            var second = LordMessageText.FromText("second"); bindings.Bind(slot, second);
            Assert.IsTrue(bindings.TryGet(slot, out text)); Assert.AreSame(second, text);
            bindings.Clear();
            Assert.IsFalse(bindings.HasBinding, "A session/init/non-Lord Add clears the optional text.");
            bindings.Bind(null, second); bindings.Bind(slot, null);
            Assert.IsFalse(bindings.HasBinding);
        }

        [TestMethod]
        public void DelayedSessionCleanupAndOldTextPublicationCannotRetireANewerMessage()
        {
            var bindings = new LordMessageTextBindings();
            var slot = new OnScreenText.OST();
            bindings.Reset(10);
            bindings.Bind(slot, LordMessageText.FromText("old"), 10);
            bindings.Reset(11);
            bindings.Bind(slot, LordMessageText.FromText("new"), 11);
            bindings.Reset(10);
            Assert.IsTrue(bindings.TryGet(slot, out var text)); Assert.AreEqual("new", text.Value);
            bindings.Bind(slot, LordMessageText.FromText("late old publication"), 10);
            Assert.IsTrue(bindings.TryGet(slot, out text)); Assert.AreEqual("new", text.Value);
            bindings.Reset(11);
            Assert.IsFalse(bindings.HasBinding, "The current session's actual end still retires its message.");
        }

        [TestMethod]
        public void InstalledOstBodyStillGeneratesWithTheRealExtenderSubtitleIlTransform()
        {
            // The installed SE was built against Cecil 0.11, while the installed MonoMod/BepInEx
            // backend and these fixtures use 0.10. Load a private in-memory assembly copy with only
            // its Cecil reference rebound, including its compiler-generated predicate signatures.
            // The installed manipulator and every lambda instruction remain unchanged. This models
            // the existing Unity Mono identity unification without changing either installed binary.
            Assembly fixtureAssembly;
            using (var module = ModuleDefinition.ReadModule(typeof(SHCDESE.API.GameAIManagerAPI).Assembly.Location))
            using (var bytes = new MemoryStream())
            {
                module.Assembly.Name.Name += ".LordMessageFixture";
                foreach (var reference in module.AssemblyReferences)
                    if (reference.Name == typeof(ModuleDefinition).Assembly.GetName().Name)
                        reference.Version = typeof(ModuleDefinition).Assembly.GetName().Version;
                module.Write(bytes); fixtureAssembly = Assembly.Load(bytes.ToArray());
            }
            var extender = fixtureAssembly.GetType("SHCDESE.ManagedHooks.ILHooks.ILHooksManager", true);
            var manipulator = extender.GetMethod("OnScreenText_UpdateOST_ILHook", BindingFlags.Instance | BindingFlags.Public);
            Assert.IsNotNull(manipulator);
            // Its empty constructor does not install hooks; execute only its published IL manipulator.
            var owner = Activator.CreateInstance(extender, true);
            {
                using (var definition = new DynamicMethodDefinition(typeof(OnScreenText).GetMethod("updateOST")))
                using (var context = new ILContext(definition.Definition))
                {
                    context.ReferenceBag = RuntimeILReferenceBag.Instance;
                    int count = context.Body.Instructions.Count;
                    context.Invoke(il => manipulator.Invoke(owner, new object[] { il }));
                    Assert.IsTrue(context.Body.Instructions.Count > count, "The installed SE subtitle transform must actually insert its callback.");
                    Assert.IsNotNull(definition.Generate().CreateDelegate(typeof(Action<OnScreenText, EngineInterface.PlayState, bool>)));
                }
            }
            using (var wrapper = new DynamicMethodDefinition(typeof(LordMessageRuntime).GetMethod("Update", PrivateStatic)))
                Assert.IsNotNull(wrapper.Generate().CreateDelegate(typeof(Action<OnScreenText, EngineInterface.PlayState, bool>)));
        }

        [TestMethod]
        public void InstalledGameMapTransformationComposesWithExistingAudioInEitherOrder()
        {
            var method = typeof(GameMap).GetMethod("processTestMap");
            LordMessageIlContract.Validate(method);
            foreach (bool audioFirst in new[] { false, true })
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                context.Invoke(il => {
                    if (audioFirst) SoundIlContract.Apply(il);
                    LordMessageIlContract.Apply(il);
                    if (!audioFirst) SoundIlContract.Apply(il);
                });
                var replacements = context.Body.Instructions.Where(i => i.Operand is MethodReference call &&
                    call.DeclaringType.FullName == typeof(LordMessageRuntime).FullName &&
                    (call.Name == "Text" || call.Name == "Speech" || call.Name == "Video")).ToArray();
                Assert.AreEqual(4, replacements.Length);
                foreach (var instruction in replacements) Assert.AreEqual(OpCodes.Call, instruction.OpCode);
                Assert.IsNotNull(definition.Generate().CreateDelegate(typeof(Action<GameMap, short[], int, EngineInterface.PlayState, byte[]>)));
                var before = context.Body.Instructions.Select(i => i.ToString()).ToArray();
                Assert.ThrowsExactly<InvalidOperationException>(() => LordMessageIlContract.Apply(context));
                CollectionAssert.AreEqual(before, context.Body.Instructions.Select(i => i.ToString()).ToArray());
            }
        }

        [TestMethod]
        public void IncomingBranchesOrChangedInstalledCallsRejectWithoutPartialMutation()
        {
            var method = typeof(GameMap).GetMethod("processTestMap");
            foreach (string change in new[] { "branch", "switch", "signature" })
            using (var definition = new DynamicMethodDefinition(method))
            using (var context = new ILContext(definition.Definition))
            {
                var sites = LordMessageIlContract.Find(context);
                if (change == "signature") sites.Speech.Operand = context.Import(typeof(SFXManager).GetMethod("playBink"));
                else context.Body.Instructions.Insert(0, change == "branch" ?
                    Instruction.Create(OpCodes.Br, sites.Video) : Instruction.Create(OpCodes.Switch, new[] { sites.Text[1] }));
                var before = context.Body.Instructions.Select(i => i.ToString()).ToArray();
                Assert.ThrowsExactly<InvalidOperationException>(() => LordMessageIlContract.Apply(context));
                CollectionAssert.AreEqual(before, context.Body.Instructions.Select(i => i.ToString()).ToArray());
            }
        }

        [TestMethod]
        public void ActualRenderWrapperHasAllocationFreeDormantAndNestedPassThrough()
        {
            using (var ready = new RuntimeStatusFixture())
            using (var original = new HookOriginal("renderHook", nameof(RecordRender)))
            using (var definition = new DynamicMethodDefinition(typeof(LordMessageRuntime).GetMethod("RenderMessages", PrivateStatic)))
            {
                var render = (Action<GameMap, short[], int, EngineInterface.PlayState, byte[]>)definition.Generate()
                    .CreateDelegate(typeof(Action<GameMap, short[], int, EngineInterface.PlayState, byte[]>));
                Assert.IsFalse(LordMessageEvents.Registry.CanNotify, "Other tests must retire their process registrations.");
                var records = Group(AILords.SK_RAT, AILordMessageType.RequestGoods);
                var state = new EngineInterface.PlayState();
                for (int i = 0; i < 1000; i++) render(null, records, 4, state, null);
                int count = renderCalls;
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) render(null, records, 4, state, null);
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0L, allocated);
                Assert.AreEqual(5000, renderCalls - count);
                string owner = "dormant." + Guid.NewGuid().ToString("N");
                for (int i = 0; i < 16; i++)
                {
                    Assert.IsTrue(LordMessageEvents.Register(owner, i.ToString(), pre => Assert.Fail("Disabled callback ran."), null,
                        out var disabled, out _));
                    disabled.SetEnabled(false);
                }
                for (int i = 0; i < 1000; i++) render(null, records, 4, state, null);
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 5000; i++) render(null, records, 4, state, null);
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Assert.AreEqual(0L, allocated, "Ready broker with disabled registrations must retain its allocation-free original path.");
                // With an enabled registration, a buffer without a complete message header still allocates nothing.
                var plain = new short[LordMessageGroups.Words * 2]; plain[0] = 5; plain[LordMessageGroups.Words] = -1;
                Assert.IsTrue(LordMessageEvents.Register(owner, "enabled", pre => Assert.Fail("This buffer has no message group."), null,
                    out var enabled, out string reason), reason);
                try
                {
                    for (int i = 0; i < 1000; i++) render(null, plain, -1, state, null);
                    before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 5000; i++) render(null, plain, -1, state, null);
                    allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                    Assert.AreEqual(0L, allocated, "No message header: no frame is allocated.");
                }
                finally { enabled.SetEnabled(false); }
                count = renderCalls;
                var registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
                var frame = LordMessageRuntime.Begin(records, 4, state, registry);
                try { render(null, records, 4, state, null); Assert.AreEqual(count + 1, renderCalls); }
                finally { LordMessageRuntime.End(frame); }
            }
        }

        private static int renderCalls;
        private static void RecordRender(GameMap self, short[] records, int count, EngineInterface.PlayState state, byte[] radar)
        { renderCalls++; }

        private sealed class RuntimeStatusFixture : IDisposable
        {
            private readonly FieldInfo managed = typeof(LordMessageRuntime).GetField("status", PrivateStatic);
            private readonly FieldInfo protocol = typeof(LordMessageRuntime).GetField("protocolStatus", PrivateStatic);
            // Readiness is read from allocation-free flags: managed hooks, the current IL edit and the audited protocol.
            private readonly FieldInfo[] flags = { typeof(LordMessageRuntime).GetField("managedReady", PrivateStatic),
                typeof(LordMessageRuntime).GetField("ilApplied", PrivateStatic), typeof(LordMessageRuntime).GetField("protocolReady", PrivateStatic) };
            private readonly object oldManaged, oldProtocol;
            private readonly object[] oldFlags;
            internal RuntimeStatusFixture()
            {
                oldManaged = managed.GetValue(null); oldProtocol = protocol.GetValue(null);
                oldFlags = flags.Select(flag => flag.GetValue(null)).ToArray();
                managed.SetValue(null, "Ready"); protocol.SetValue(null, "Ready");
                foreach (var flag in flags) flag.SetValue(null, 1);
            }
            public void Dispose()
            {
                managed.SetValue(null, oldManaged); protocol.SetValue(null, oldProtocol);
                for (int i = 0; i < flags.Length; i++) flags[i].SetValue(null, oldFlags[i]);
            }
        }

        [TestMethod]
        public void InstalledTransformedMessageCasesExecuteProductionSelectionAndKeepOriginalFlagsAndOstSelectors()
        {
            using (var fixture = new InstalledMessageFixture())
            {
                int pres = 0, posts = 0;
                LordMessageEvents.Register("installed", "replace", pre => {
                    pres++;
                    Assert.IsNull(pre.Context.PlayerId);
                    pre.TryReplaceAudio(new LordMessageAudio(LordMessageSourceKind.Vanilla, "donor.wav"));
                    pre.TryReplaceVideo(new LordMessageVideo(LordMessageSourceKind.Vanilla, "donor.bik"));
                    pre.TryReplaceText(LordMessageText.FromText("actual message text"));
                }, post => posts++, out _, out _, registry: fixture.Registry);
                var records = Group(AILords.SK_RAT, AILordMessageType.RequestGoods);
                var before = (short[])records.Clone();
                fixture.Run(records);
                Assert.AreEqual(1, pres); Assert.AreEqual(1, posts);
                Assert.AreEqual("donor.wav", fixture.AudioPath); Assert.AreEqual("donor.bik", fixture.VideoPath);
                Assert.AreEqual(1, fixture.Channel); Assert.AreEqual(1f, fixture.Volume);
                Assert.IsFalse(fixture.Loop); Assert.IsTrue(fixture.WaitForSpeech);
                Assert.AreEqual(1, fixture.AudioCalls); Assert.AreEqual(1, fixture.VideoCalls);
                var slot = fixture.MessageSlot;
                Assert.AreEqual((int)Enums.eTextSections.TEXT_SKIRMISH_SPEECH, slot.data1);
                Assert.AreEqual((int)AILordMessageType.RequestGoods, slot.data2);
                Assert.IsTrue(LordMessageRuntime.TextBindings.TryGet(slot, out var text));
                Assert.AreEqual("actual message text", text.Value);
                CollectionAssert.AreEqual(before, records);
                fixture.AddOrdinaryMessage();
                Assert.IsFalse(LordMessageRuntime.TextBindings.HasBinding, "The real Add wrapper clears a reused slot for a non-Lord message.");
            }
        }

        [TestMethod]
        public void ActualMediaExceptionIsNotRetriedOrReportedAsSuccessfulPresentation()
        {
            using (var fixture = new InstalledMessageFixture())
            {
                int posts = 0;
                LordMessageEvents.Register("installed", "throw", null, post => posts++, out _, out _, registry: fixture.Registry);
                fixture.ThrowAudio = true;
                Assert.ThrowsExactly<InvalidOperationException>(() => fixture.Run(Group(AILords.SK_WOLF, AILordMessageType.RequestGoods)));
                Assert.AreEqual(1, fixture.AudioCalls);
                Assert.AreEqual(0, fixture.VideoCalls); Assert.AreEqual(0, posts);
                fixture.ThrowAudio = false;
                fixture.Run(Group(AILords.SK_WOLF, AILordMessageType.RequestGoods));
                Assert.AreEqual(2, fixture.AudioCalls); Assert.AreEqual(1, fixture.VideoCalls); Assert.AreEqual(1, posts);
            }
        }

        [TestMethod]
        public void RealOstChangedBranchRestoresKeepTextForTheSameActiveSelectorsAfterCustomText()
        {
            using (var fixture = new InstalledMessageFixture())
            {
                LordMessageEvents.Register("ost", "replace", pre => pre.TryReplaceText(LordMessageText.FromText("custom B")),
                    null, out var registration, out _, registry: fixture.Registry);
                var records = Group(AILords.SK_RAT, AILordMessageType.RequestGoods);
                fixture.Run(records);
                fixture.MessageSlot.active = true;
                fixture.RenderCurrentText();
                Assert.AreEqual("custom B", fixture.RenderedText);
                Assert.IsFalse(fixture.MessageSlot.wasTurnedOnOrChanged, "The real getOST consumed the original changed flag.");
                registration.SetEnabled(false);
                fixture.Run(records);
                Assert.IsTrue(fixture.MessageSlot.wasTurnedOnOrChanged, "Retiring custom text requires one ordinary redraw, even for identical selectors.");
                fixture.RenderCurrentText();
                Assert.AreEqual("vanilla:" + (int)Enums.eTextSections.TEXT_SKIRMISH_SPEECH + ":" + (int)AILordMessageType.RequestGoods, fixture.RenderedText);
                Assert.IsFalse(LordMessageRuntime.TextBindings.HasBinding);
            }
        }

        [TestMethod]
        public void RealOstChangedBranchRestoresNonLordTextAndUnboundAddsKeepTheirOriginalChangePolicy()
        {
            using (var fixture = new InstalledMessageFixture())
            {
                fixture.MessageSlot.active = true;
                fixture.MessageSlot.wasTurnedOnOrChanged = false;
                fixture.AddOrdinaryMessage();
                Assert.IsFalse(fixture.MessageSlot.wasTurnedOnOrChanged, "No custom binding means unchanged Vanilla Add behavior.");
                LordMessageEvents.Register("ost", "replace", pre => pre.TryReplaceText(LordMessageText.FromText("custom B")),
                    null, out _, out _, registry: fixture.Registry);
                fixture.Run(Group(AILords.SK_RAT, AILordMessageType.RequestGoods));
                fixture.MessageSlot.wasTurnedOnOrChanged = true;
                fixture.RenderCurrentText();
                Assert.AreEqual("custom B", fixture.RenderedText);
                fixture.AddOrdinaryMessage();
                Assert.IsTrue(fixture.MessageSlot.wasTurnedOnOrChanged);
                fixture.RenderCurrentText();
                Assert.AreEqual("vanilla:17:4", fixture.RenderedText);
                Assert.IsFalse(LordMessageRuntime.TextBindings.HasBinding);
            }
        }

        private delegate void TextCall(OnScreenText self, Enums.eOnScreenText id, int a, int b, int c, int d, int e, int index);
        private delegate void AddCall(OnScreenText self, Enums.eOnScreenText id, int a, int b, int c, int d, int e);
        private delegate void SpeechCall(SFXManager self, int channel, string path, float volume, bool ignoreMuting, bool ignorePause, int index);
        private delegate void VideoCall(SFXManager self, string path, bool loop, bool wait, int index);
        private delegate OnScreenText.OST GetOstCall(OnScreenText self, Enums.eOnScreenText id, ref bool off, ref bool changed, bool handleChange);

        // These slices retain the installed GameMap case instructions and the production IL patch.
        // Unrelated map/tile/camera cases are not executable on desktop CLR. Only external output
        // methods are replaced; parsing, target composition, original arguments and call order stay real.
        private sealed class InstalledMessageFixture : IDisposable
        {
            private static InstalledMessageFixture current;
            private readonly FieldInfo slots = typeof(OnScreenText).GetField("OSTs", BindingFlags.Instance | BindingFlags.NonPublic);
            private readonly FieldInfo runtimeSlots = typeof(LordMessageRuntime).GetField("slots", PrivateStatic);
            private readonly object previousSlots;
            private readonly HookOriginal originalAdd;
            private readonly HookOriginal originalUpdate;
            private readonly AddCall add;
            private readonly TextCall text;
            private readonly SpeechCall speech;
            private readonly VideoCall video;
            private readonly GetOstCall getOst;
            private readonly Action<OnScreenText, EngineInterface.PlayState, bool> update;
            private readonly Action<GameMap, short[], int, EngineInterface.PlayState, byte[]> textCase, speechCase, videoCase;
            private readonly OnScreenText screen = (OnScreenText)FormatterServices.GetUninitializedObject(typeof(OnScreenText));
            internal readonly InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs> Registry = new InterceptionEvent<LordMessageMutation, LordMessagePostEventArgs>();
            internal int AudioCalls, VideoCalls, Channel;
            internal float Volume;
            internal bool Loop, WaitForSpeech, ThrowAudio;
            internal string AudioPath, VideoPath;
            internal string RenderedText;
            private readonly EngineInterface.PlayState uiState = new EngineInterface.PlayState();
            internal OnScreenText.OST MessageSlot => ((OnScreenText.OST[])slots.GetValue(screen))[(int)Enums.eOnScreenText.OST_MESSAGE_BAR];
            internal InstalledMessageFixture()
            {
                current = this;
                var array = Enumerable.Range(0, 32).Select(i => new OnScreenText.OST { ostID = i }).ToArray();
                slots.SetValue(screen, array);
                previousSlots = runtimeSlots.GetValue(null); runtimeSlots.SetValue(null, slots);
                LordMessageRuntime.TextBindings.Clear();
                var original = Copy<AddCall>(typeof(OnScreenText).GetMethod("addOSTEntry"), null);
                originalAdd = new HookOriginal("addHook", original);
                add = Copy<AddCall>(typeof(LordMessageRuntime).GetMethod("AddText", PrivateStatic), null);
                text = Copy<TextCall>(typeof(LordMessageRuntime).GetMethod("Text", PrivateStatic), called =>
                    called.DeclaringType.FullName == "OnScreenText" && called.Name == "addOSTEntry" ? nameof(AddBoundary) : null);
                speech = Copy<SpeechCall>(typeof(LordMessageRuntime).GetMethod("Speech", PrivateStatic), called =>
                    called.DeclaringType.FullName == "SFXManager" && called.Name == "playSpeech" ? nameof(SpeechBoundary) : null);
                video = Copy<VideoCall>(typeof(LordMessageRuntime).GetMethod("Video", PrivateStatic), called =>
                    called.DeclaringType.FullName == "SFXManager" && called.Name == "playBink" ? nameof(VideoBoundary) : null);
                getOst = Copy<GetOstCall>(typeof(OnScreenText).GetMethod("getOST"), null);
                originalUpdate = new HookOriginal("updateHook", SliceOstUpdate());
                update = Copy<Action<OnScreenText, EngineInterface.PlayState, bool>>(typeof(LordMessageRuntime).GetMethod("Update", PrivateStatic), UiBoundary);
                textCase = Slice("Text"); speechCase = Slice("Speech"); videoCase = Slice("Video");
            }
            internal void Run(short[] records)
            {
                var state = new EngineInterface.PlayState { speechFileName = "original.wav", binkFileName = "original.bik" };
                var frame = LordMessageRuntime.Begin(records, records.Length / 21, state, Registry);
                try { textCase(null, records, 0, state, null); speechCase(null, records, 1, state, null); videoCase(null, records, 3, state, null); }
                finally { LordMessageRuntime.End(frame); }
            }
            internal void AddOrdinaryMessage() => add(screen, Enums.eOnScreenText.OST_MESSAGE_BAR, 17, 4, 0, 0, 0);
            internal void RenderCurrentText() => update(screen, uiState, false);
            private static T Copy<T>(MethodInfo source, Func<MethodReference, string> boundary) where T : Delegate
            {
                using (var definition = new DynamicMethodDefinition(source))
                {
                    foreach (var instruction in definition.Definition.Body.Instructions)
                    {
                        if (!(instruction.Operand is MethodReference called)) continue;
                        string replacement = boundary?.Invoke(called); if (replacement == null) continue;
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = definition.Definition.Module.ImportReference(typeof(InstalledMessageFixture).GetMethod(replacement, PrivateStatic));
                    }
                    return (T)definition.Generate().CreateDelegate(typeof(T));
                }
            }
            private static Action<GameMap, short[], int, EngineInterface.PlayState, byte[]> Slice(string component)
            {
                using (var definition = new DynamicMethodDefinition(typeof(GameMap).GetMethod("processTestMap")))
                using (var context = new ILContext(definition.Definition))
                {
                    var sites = LordMessageIlContract.Find(context);
                    var selected = component == "Text" ? sites.Text[1] : component == "Speech" ? sites.Speech : sites.Video;
                    var original = context.Body.Instructions.ToArray();
                    int call = Array.IndexOf(original, selected);
                    var targets = original.SelectMany(i => i.Operand is Instruction[] many ? many : Array.Empty<Instruction>())
                        .Select(i => Array.IndexOf(original, i)).Where(i => i >= 0).Distinct().OrderBy(i => i).ToArray();
                    int start = targets.Last(i => i <= call), end = targets.First(i => i > call);
                    var retained = new HashSet<Instruction>(original.Skip(start).Take(end - start));
                    context.Invoke(LordMessageIlContract.Apply);
                    // Include the appended record-index load immediately before every changed call.
                    var patched = context.Body.Instructions.ToArray();
                    int patchedStart = Array.IndexOf(patched, original[start]);
                    int patchedEnd = Array.IndexOf(patched, original[end]);
                    var slice = patched.Skip(patchedStart).Take(patchedEnd - patchedStart).ToArray();
                    var keep = new HashSet<Instruction>(slice);
                    var ret = Instruction.Create(OpCodes.Ret);
                    foreach (var instruction in slice)
                    {
                        if (instruction.Operand is Instruction target && !keep.Contains(target)) instruction.Operand = ret;
                        if (instruction.Operand is MethodReference called)
                        {
                            string name = called.DeclaringType.FullName == typeof(LordMessageRuntime).FullName ? "Forward" + called.Name :
                                called.DeclaringType.FullName == "OnScreenText" && called.Name == "get_Instance" ? nameof(ScreenBoundary) :
                                called.DeclaringType.FullName == "SFXManager" && called.Name == "stopBink" ? nameof(StopBoundary) : null;
                            if (name != null)
                            { instruction.OpCode = OpCodes.Call; instruction.Operand = context.Import(typeof(InstalledMessageFixture).GetMethod(name, PrivateStatic)); }
                        }
                    }
                    context.Body.Instructions.Clear();
                    context.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
                    context.Body.Instructions.Add(Instruction.Create(OpCodes.Stloc, sites.Index));
                    foreach (var instruction in slice) context.Body.Instructions.Add(instruction);
                    context.Body.Instructions.Add(ret);
                    context.Body.ExceptionHandlers.Clear();
                    return (Action<GameMap, short[], int, EngineInterface.PlayState, byte[]>)definition.Generate()
                        .CreateDelegate(typeof(Action<GameMap, short[], int, EngineInterface.PlayState, byte[]>));
                }
            }
            private static Action<OnScreenText, EngineInterface.PlayState, bool> SliceOstUpdate()
            {
                using (var definition = new DynamicMethodDefinition(typeof(OnScreenText).GetMethod("updateOST")))
                {
                    var body = definition.Definition.Body;
                    var original = body.Instructions.ToArray();
                    var calls = original.Select((instruction, index) => new { instruction, index }).Where(item =>
                        item.instruction.Operand is MethodReference called && called.DeclaringType.FullName == "OnScreenText" && called.Name == "getOST").ToArray();
                    var message = calls.Single(item => item.index >= 5 && original[item.index - 4].MatchLdcI4((int)Enums.eOnScreenText.OST_MESSAGE_BAR));
                    int start = message.index - 5;
                    int end = calls.First(item => item.index > message.index).index - 5;
                    Assert.IsTrue(original[message.index - 1].MatchLdloc(out int handleChange));
                    var slice = original.Skip(start).Take(end - start).ToArray();
                    var keep = new HashSet<Instruction>(slice);
                    var ret = Instruction.Create(OpCodes.Ret);
                    foreach (var instruction in slice)
                    {
                        if (instruction.Operand is Instruction target && !keep.Contains(target)) instruction.Operand = ret;
                        if (!(instruction.Operand is MethodReference called)) continue;
                        string replacement = UiBoundary(called);
                        if (replacement == null) continue;
                        instruction.OpCode = OpCodes.Call;
                        instruction.Operand = definition.Definition.Module.ImportReference(typeof(InstalledMessageFixture).GetMethod(replacement, PrivateStatic));
                    }
                    body.Instructions.Clear();
                    body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                    body.Instructions.Add(Instruction.Create(OpCodes.Stloc, body.Variables[handleChange]));
                    foreach (var instruction in slice) body.Instructions.Add(instruction);
                    body.Instructions.Add(ret); body.ExceptionHandlers.Clear();
                    return (Action<OnScreenText, EngineInterface.PlayState, bool>)definition.Generate()
                        .CreateDelegate(typeof(Action<OnScreenText, EngineInterface.PlayState, bool>));
                }
            }
            private static string UiBoundary(MethodReference called)
            {
                if (called.DeclaringType.FullName == "OnScreenText" && called.Name == "getOST") return nameof(GetOstBoundary);
                if (called.DeclaringType.FullName == typeof(MainViewModel).FullName)
                {
                    switch (called.Name)
                    {
                        case "get_Instance": return nameof(ModelBoundary);
                        case "get_Show_HUD_Briefing": return nameof(BriefingBoundary);
                        case "get_OST_Message_Bar_Text": return nameof(CurrentTextBoundary);
                        case "set_OST_Message_Bar_Text": return nameof(TextBoundary);
                        case "set_OST_Message_Bar_Vis": return nameof(VisibleBoundary);
                        case "set_OST_Message_Bar_Margin": return nameof(MarginBoundary);
                    }
                }
                if (called.DeclaringType.FullName == typeof(GameData).FullName)
                    return called.Name == "get_Instance" ? nameof(GameBoundary) : called.Name == "get_lastGameState" ? nameof(StateBoundary) : null;
                if (called.DeclaringType.FullName == typeof(Translate).FullName)
                    return called.Name == "get_Instance" ? nameof(TranslateBoundary) : called.Name == "lookUpText" ? nameof(TranslationBoundary) : null;
                return null;
            }
            private static OnScreenText.OST GetOstBoundary(OnScreenText self, Enums.eOnScreenText id, ref bool off, ref bool changed, bool handleChange)
                => current.getOst(self, id, ref off, ref changed, handleChange);
            private static MainViewModel ModelBoundary() => null;
            private static bool BriefingBoundary(MainViewModel self) => false;
            private static string CurrentTextBoundary(MainViewModel self) => current.RenderedText;
            private static void TextBoundary(MainViewModel self, string value) { current.RenderedText = value; }
            private static void VisibleBoundary(MainViewModel self, bool value) { }
            private static void MarginBoundary(MainViewModel self, string value) { }
            private static GameData GameBoundary() => null;
            private static EngineInterface.PlayState StateBoundary(GameData self) => current.uiState;
            private static Translate TranslateBoundary() => null;
            private static string TranslationBoundary(Translate self, Enums.eTextSections section, int index) => "vanilla:" + (int)section + ":" + index;
            private static OnScreenText ScreenBoundary() => current.screen;
            private static void AddBoundary(OnScreenText self, Enums.eOnScreenText id, int a, int b, int c, int d, int e) => current.add(self, id, a, b, c, d, e);
            private static void ForwardText(OnScreenText self, Enums.eOnScreenText id, int a, int b, int c, int d, int e, int index) => current.text(self, id, a, b, c, d, e, index);
            private static void ForwardSpeech(SFXManager self, int channel, string path, float volume, bool ignoreMuting, bool ignorePause, int index) => current.speech(self, channel, path, volume, ignoreMuting, ignorePause, index);
            private static void ForwardVideo(SFXManager self, string path, bool loop, bool wait, int index) => current.video(self, path, loop, wait, index);
            private static void SpeechBoundary(SFXManager self, int channel, string path, float volume, bool ignoreMuting, bool ignorePause)
            { current.AudioCalls++; current.Channel = channel; current.AudioPath = path; current.Volume = volume; if (current.ThrowAudio) throw new InvalidOperationException("actual output edge failure"); }
            private static void VideoBoundary(SFXManager self, string path, bool loop, bool wait)
            { current.VideoCalls++; current.VideoPath = path; current.Loop = loop; current.WaitForSpeech = wait; }
            private static void StopBoundary(SFXManager self) { }
            public void Dispose()
            {
                originalUpdate?.Dispose(); originalAdd?.Dispose(); runtimeSlots.SetValue(null, previousSlots); LordMessageRuntime.TextBindings.Clear(); current = null;
            }
        }

        // Swap only the original external edge of a private runtime hook; no live detour is installed.
        private sealed class HookOriginal : IDisposable
        {
            private readonly object hook;
            private readonly PropertyInfo original;
            private readonly object previous;
            internal HookOriginal(string field, string recorder)
                : this(field, typeof(LordMessageTests).GetMethod(recorder, PrivateStatic)) { }
            private HookOriginal(string field, MethodInfo recorder)
            {
                hook = typeof(LordMessageRuntime).GetField(field, PrivateStatic).GetValue(null);
                original = hook.GetType().GetProperty("Original", BindingFlags.Instance | BindingFlags.NonPublic);
                previous = original.GetValue(hook);
                original.SetValue(hook, Delegate.CreateDelegate(original.PropertyType, recorder));
            }
            internal HookOriginal(string field, Delegate recorder)
            {
                hook = typeof(LordMessageRuntime).GetField(field, PrivateStatic).GetValue(null);
                original = hook.GetType().GetProperty("Original", BindingFlags.Instance | BindingFlags.NonPublic);
                previous = original.GetValue(hook);
                var parameters = original.PropertyType.GetMethod("Invoke").GetParameters()
                    .Select(p => System.Linq.Expressions.Expression.Parameter(p.ParameterType, p.Name)).ToArray();
                var invoke = System.Linq.Expressions.Expression.Invoke(System.Linq.Expressions.Expression.Constant(recorder), parameters);
                original.SetValue(hook, System.Linq.Expressions.Expression.Lambda(original.PropertyType, invoke, parameters).Compile());
            }
            public void Dispose() { original.SetValue(hook, previous); }
        }

        private static short[] Group(AILords lord, AILordMessageType type)
        {
            var records = new short[4 * 21];
            records[0] = 20;
            records[11] = (short)Enums.eTextSections.TEXT_SKIRMISH_SPEECH;
            records[12] = (short)(((int)lord - (int)AILords.SK_RAT) * 34 + (int)type);
            records[13] = records[14] = 1;
            records[21] = 18; records[21 + 11] = 100; records[21 + 12] = 1;
            records[42] = 22;
            records[63] = 21; records[63 + 3] = 0; records[63 + 11] = 1;
            return records;
        }
    }
}
