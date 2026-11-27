using System;
using System.Threading;
using APIShared.Simulation;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace APISharedTests
{
    [TestClass]
    public sealed class SimulationMutationScopeTests
    {
        [TestMethod]
        public void AdmissionIsNestedThreadBoundAndRetiredAfterException()
        {
            Assert.IsFalse(SimulationMutationScope.IsActive);
            try
            {
                using (SimulationMutationScope.Enter())
                {
                    Assert.IsTrue(SimulationMutationScope.IsActive);
                    bool workerAdmitted = true;
                    var worker = new Thread(() => workerAdmitted = SimulationMutationScope.IsActive);
                    worker.Start(); worker.Join();
                    Assert.IsFalse(workerAdmitted);
                    using (SimulationMutationScope.Enter()) Assert.IsTrue(SimulationMutationScope.IsActive);
                    Assert.IsTrue(SimulationMutationScope.IsActive);
                    throw new InvalidOperationException("fixture");
                }
            }
            catch (InvalidOperationException) { }
            Assert.IsFalse(SimulationMutationScope.IsActive);
        }
    }
}
