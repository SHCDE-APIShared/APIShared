// External Noesis dependency-property boundary only; production sizing and pixel math are source linked.
namespace Noesis {
 public class DependencyPropertyChangedEventArgs:System.EventArgs {public object NewValue;public DependencyPropertyChangedEventArgs(object value){NewValue=value;}}
 public class PropertyMetadata {internal object Default;internal System.Action<DependencyObject,DependencyPropertyChangedEventArgs> Changed;public PropertyMetadata(object value,System.Action<DependencyObject,DependencyPropertyChangedEventArgs> changed){Default=value;Changed=changed;}}
 public class DependencyProperty {internal PropertyMetadata Metadata;public static DependencyProperty RegisterAttached(string name,System.Type type,System.Type owner,PropertyMetadata metadata)=>new(){Metadata=metadata};}
 public class DependencyObject {
  private readonly Dictionary<DependencyProperty,object> values=new();
  public object GetValue(DependencyProperty property)=>values.TryGetValue(property,out var value)?value:property.Metadata.Default;
  public void SetValue(DependencyProperty property,object value){if(Equals(GetValue(property),value))return;values[property]=value;property.Metadata.Changed(this,new DependencyPropertyChangedEventArgs(value));}
 }
 public partial class FrameworkElement:DependencyObject { }
 public static class VisualTreeHelper {public static DependencyObject GetParent(DependencyObject source)=>(source as FrameworkElement)?.Parent;}
}
namespace APIShared.ModSettings {
 // Resolution presets are an existing unrelated screen boundary, not the new source-reference algorithm.
 public static class ToolTipPresentation {public static float AutomaticFontSize=>23;public static float AutomaticMaximumWidth=>780;}
}
namespace APIShared.Internal {
 internal static class ToolTipPresentation {internal static Noesis.Thickness AutomaticPadding=>new(24,18,24,18);internal static Noesis.Thickness AutomaticBorderThickness=>new(4.5);}
}
