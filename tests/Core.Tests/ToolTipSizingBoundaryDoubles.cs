// External Noesis dependency-property boundary only; production sizing and pixel math are source linked.
namespace Noesis {
 public class DependencyPropertyChangedEventArgs:System.EventArgs {public object NewValue;public DependencyPropertyChangedEventArgs(object value){NewValue=value;}}
 public class PropertyMetadata {internal object Default;internal System.Action<DependencyObject,DependencyPropertyChangedEventArgs> Changed;public PropertyMetadata(object value,System.Action<DependencyObject,DependencyPropertyChangedEventArgs> changed){Default=value;Changed=changed;}}
 public class DependencyProperty {public static readonly object UnsetValue=new();internal PropertyMetadata Metadata;public static DependencyProperty RegisterAttached(string name,System.Type type,System.Type owner,PropertyMetadata metadata)=>new(){Metadata=metadata};}
 public class DependencyObject {
  private readonly Dictionary<DependencyProperty,object> values=new();
  internal System.Exception ReadLocalFailure;
  public object GetValue(DependencyProperty property){var value=values.TryGetValue(property,out var local)?local:property.Metadata.Default;return value is BindingBase binding?binding.Value:value;}
  public object ReadLocalValue(DependencyProperty property){if(ReadLocalFailure!=null)throw ReadLocalFailure;return values.TryGetValue(property,out var value)?value:DependencyProperty.UnsetValue;}
  public void ClearValue(DependencyProperty property){var before=GetValue(property);values.Remove(property);var after=GetValue(property);if(!Equals(before,after))property.Metadata.Changed(this,new DependencyPropertyChangedEventArgs(after));}
  public void SetValue(DependencyProperty property,object value){var before=GetValue(property);values[property]=value;var after=GetValue(property);if(!Equals(before,after))property.Metadata.Changed(this,new DependencyPropertyChangedEventArgs(after));}
 }
 public class BindingBase {public object Value;}
 public static class BindingOperations {public static BindingBase GetBindingBase(DependencyObject target,DependencyProperty property)=>target.ReadLocalValue(property) as BindingBase;public static void SetBinding(DependencyObject target,DependencyProperty property,BindingBase binding)=>target.SetValue(property,binding);}
 public class UIElement:DependencyObject {public static readonly DependencyProperty IsEnabledProperty=DependencyProperty.RegisterAttached("IsEnabled",typeof(bool),typeof(UIElement),new PropertyMetadata(true,(_,__)=>{}));}
 public partial class FrameworkElement:UIElement { }
 public static class VisualTreeHelper {public static DependencyObject GetParent(DependencyObject source)=>(source as FrameworkElement)?.Parent;}
}
namespace APIShared.ModSettings {
 // Resolution presets are an existing unrelated screen boundary, not the new source-reference algorithm.
 public static class ToolTipPresentation {public static float AutomaticFontSize=>23;public static float AutomaticMaximumWidth=>780;}
}
namespace APIShared.Internal {
 internal static class ToolTipPresentation {internal static Noesis.Thickness AutomaticPadding=>new(24,18,24,18);internal static Noesis.Thickness AutomaticBorderThickness=>new(4.5);}
}
