using APIShared.ModSettings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Noesis;

namespace Core.Tests {
 [TestClass]
 public sealed class SharedToolTipSizingTests {
  [TestMethod] public void SourceReferenceMatchesHudPixelsAndReturnsScalerOwnershipOnDisable(){
   var viewport=new Grid {ActualWidth=1000,ScreenScaleX=3,ScreenScaleY=3};
   var source=new Button {ScreenScaleX=3,ScreenScaleY=3};viewport.Children.Add(source);source.Load();
   var tip=new ToolTip {ScalerEnabled=true};
   SharedToolTipSizing.SetReferenceElement(tip,source);SharedToolTipSizing.SetReferenceFontSize(tip,16);SharedToolTipSizing.SetEnabled(tip,true);
   Assert.IsFalse(tip.ScalerEnabled);tip.IsOpen=true;Assert.AreEqual(16f,tip.FontSize);
   tip.Load();Assert.AreEqual(48f,tip.FontSize);Assert.AreEqual(1260d,tip.MaxWidth);
   int writes=tip.FontWrites;for(int i=0;i<20;i++){source.Resize();tip.Resize();viewport.Resize();}Assert.AreEqual(writes,tip.FontWrites);
   tip.ScreenScaleY=tip.ScreenScaleX=3;tip.Resize();Assert.AreEqual(16f,tip.FontSize);Assert.AreEqual(420d,tip.MaxWidth);
   SharedToolTipSizing.SetEnabled(tip,false);Assert.IsTrue(tip.ScalerEnabled);
   tip.FontSize=7;source.Resize();viewport.Resize();tip.Resize();Assert.AreEqual(7f,tip.FontSize);
  }
  [TestMethod] public void PresetUsersStayOnExistingBehaviorAndReferenceChangesAreAbsolute(){
   var tip=new ToolTip();SharedToolTipSizing.SetEnabled(tip,true);Assert.AreEqual(23f,tip.FontSize);Assert.AreEqual(780d,tip.MaxWidth);
   var source=new Button {ScreenScaleX=2,ScreenScaleY=2};source.Load();tip.Load();tip.IsOpen=true;
   SharedToolTipSizing.SetReferenceElement(tip,source);Assert.AreEqual(32f,tip.FontSize);
   SharedToolTipSizing.SetReferenceFontSize(tip,18);Assert.AreEqual(36f,tip.FontSize);
   source.ScreenScaleY=float.NaN;source.Resize();Assert.AreEqual(36f,tip.FontSize);
   SharedToolTipSizing.SetReferenceElement(tip,null);Assert.AreEqual(23f,tip.FontSize);Assert.AreEqual(780d,tip.MaxWidth);
   SharedToolTipSizing.SetEnabled(tip,false);
  }
 }
}
