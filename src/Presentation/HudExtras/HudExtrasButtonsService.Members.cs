using System;

namespace APIShared
{
    // Kept out of HudExtrasButtonsService.cs, which Core.Tests compiles against SDK doubles.
    internal sealed partial class HudExtrasButtonsService
    {
        // Script Extender and game members this service's own code binds; checked once at creation (issue #7).
        internal static ManagedMemberContract MemberContract() => new ManagedMemberContract()
            .Field(typeof(global::CrusaderDE.MainViewModel), "viewModelLoaded", typeof(bool))
            .Method(typeof(global::CrusaderDE.MainViewModel), "add_PropertyChanged", typeof(void), typeof(global::System.ComponentModel.PropertyChangedEventHandler))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Instance", typeof(global::CrusaderDE.MainViewModel))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Show_HUD_Extras", typeof(bool))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Show_HUD_Extras_Button_Freebuild", typeof(bool))
            .Method(typeof(global::CrusaderDE.MainViewModel), "get_Show_HUD_Extras_Button_Objectves", typeof(bool))
            .Method(typeof(global::CrusaderDE.MainViewModel), "remove_PropertyChanged", typeof(void), typeof(global::System.ComponentModel.PropertyChangedEventHandler))
            .Method(typeof(global::SHCDESE.API.GameXAMLManagerAPI), "FindGlobalElement", typeof(global::Noesis.FrameworkElement), typeof(string))
            .Method(typeof(global::SHCDESE.API.GameXAMLManagerAPI), "get_Instance", typeof(global::SHCDESE.API.GameXAMLManagerAPI));
    }
}
