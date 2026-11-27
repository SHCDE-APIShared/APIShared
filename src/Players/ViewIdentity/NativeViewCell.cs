namespace APIShared.PlayerControl
{
    // Vanilla's native viewpoint cell (manager +0x36FA50) in the audited CrusaderDE.dll; the
    // Script Extender exposes its address as GameGlobalsManager.LocalPlayerIdVA. Shared by the
    // view identity and the spectator input boundaries. See UpdateToNewDLL.md.
    internal static class NativeViewCell
    {
        internal const int Rva = 0x88E3D70;
    }
}
