using System;
using System.Linq;
using System.Collections.Generic;
using APIShared.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SHCDESE.Interop;
using SHCDESE.Interop.Enums;

namespace APISharedTests
{
    [TestClass]
    public sealed class AutomationSetupTests
    {
        [TestMethod]
        public void EveryAcceptedNativeLordMapsToItsVanillaLobbyType()
        {
            // The names/order are the actual audited Vanilla SkirmishAIAddClick
            // builtin cases 0..28; their native AILords equivalents are 1..29.
            var expected=new[]{AILords.SK_RAT,AILords.SK_SNAKE,AILords.SK_PIG,AILords.SK_WOLF,
                AILords.SK_SALADIN,AILords.SK_CALIPH,AILords.SK_SULTAN,AILords.SK_RICHARD,
                AILords.SK_FREDERICK,AILords.SK_PHILLIP,AILords.SK_WAZIR,AILords.SK_EMIR,
                AILords.SK_NIZAR,AILords.SK_SHERIFF,AILords.SK_MARSHAL,AILords.SK_ABBOT,
                AILords.SK_JEWEL,AILords.SK_SENTINEL,AILords.SK_NOMAD,AILords.SK_KAHIN,
                AILords.SK_CANARY,AILords.SK_TRADER,AILords.SK_SERGEANT,AILords.SK_LIONESS,
                AILords.SK_CROCODILE,AILords.SK_BALDWIN,AILords.SK_BULLSEYE,AILords.SK_DLC4A,AILords.SK_DLC4B};
            CollectionAssert.AreEqual(expected,Enum.GetValues(typeof(AILords)).Cast<AILords>()
                .Where(l=>l>AILords.SK_NULL && l<AILords.SK_X1).ToArray());
            for(int lobbyType=0;lobbyType<expected.Length;lobbyType++)
                Assert.AreEqual(lobbyType,AutomationRun.SkirmishLobbyLordType(expected[lobbyType]),expected[lobbyType].ToString());
        }
        [TestMethod]
        public void SentinelsCustomSlotsAndUndefinedLordsAreRejected()
        {
            foreach(var lord in new[]{AILords.SK_NULL,AILords.SK_X1,AILords.SK_X8,AILords.SK_TEMP,(AILords)uint.MaxValue})
                Assert.ThrowsExactly<ArgumentOutOfRangeException>(()=>AutomationRun.SkirmishLobbyLordType(lord));
        }

        [TestMethod]
        public void PreparationRunsOnceBeforeValidationAndPlayUsesValidatedReceiver()
        {
            var trace=new List<string>();var ready=new object();int callbacks=0;
            AutomationRun.CommitSkirmishLaunch(()=>{callbacks++;trace.Add("prepare");},
                ()=>{trace.Add("validate");return ready;},
                receiver=>{Assert.AreSame(ready,receiver);trace.Add("play");});
            Assert.AreEqual(1,callbacks);
            CollectionAssert.AreEqual(new[]{"prepare","validate","play"},trace);
        }

        [TestMethod]
        public void ThrowingPreparationDoesNotValidateOrPlay()
        {
            int validations=0,plays=0;var failure=new InvalidOperationException("preparation");
            Assert.AreSame(failure,Assert.ThrowsExactly<InvalidOperationException>(()=>
                AutomationRun.CommitSkirmishLaunch<object>(()=>throw failure,
                    ()=>{validations++;return new object();},_=>plays++)));
            Assert.AreEqual(0,validations);Assert.AreEqual(0,plays);
        }

        [TestMethod]
        public void ChangedRoomOrMapIsRevalidatedAfterPreparationAndCannotLaunch()
        {
            foreach(bool changeRoom in new[]{true,false})
            {
                var original=new object();object current=original;string map="expected.map";int plays=0,checks=0;
                Assert.ThrowsExactly<InvalidOperationException>(()=>AutomationRun.CommitSkirmishLaunch(
                    ()=>{if(changeRoom)current=new object();else map="other.map";},
                    ()=>{checks++;if(!ReferenceEquals(current,original) || map!="expected.map")throw new InvalidOperationException();return current;},
                    _=>plays++));
                Assert.AreEqual(1,checks);Assert.AreEqual(0,plays);
            }
        }

        [TestMethod]
        public void NullPreparationRetainsValidationAndOrdinaryPlay()
        {
            var trace=new List<string>();
            AutomationRun.CommitSkirmishLaunch(null,()=>{trace.Add("validate");return 7;},
                receiver=>{Assert.AreEqual(7,receiver);trace.Add("play");});
            CollectionAssert.AreEqual(new[]{"validate","play"},trace);
        }

        [TestMethod]
        public void LegacyAndCallbackOverloadsRetainDeferredArgumentValidation()
        {
            // These iterators fail before their first game access. The existing
            // overload still accepts a third null without overload ambiguity.
            var run=(AutomationRun)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(AutomationRun));
            var old=run.StartSkirmish(null,new[]{AILords.SK_RAT},null);
            var added=run.StartSkirmish(null,new[]{AILords.SK_RAT},null,()=>Assert.Fail("Invalid arguments must not prepare"));
            using(var iterator=old.GetEnumerator())Assert.ThrowsExactly<ArgumentException>(()=>iterator.MoveNext());
            using(var iterator=added.GetEnumerator())Assert.ThrowsExactly<ArgumentException>(()=>iterator.MoveNext());
        }
    }
}
