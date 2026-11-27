using System;
using System.Reflection;
using CrusaderDE;

namespace APIShared
{
    // Vanilla's local Play and solo Coop load this exact receiver-owned header;
    // the dummy local lobby does not publish a map name/CRC. Share the existing
    // preparation reflection rather than introducing another selector or catalog.
    internal static class LobbySelectedMapHeader
    {
        private static readonly FieldInfo selected = typeof(FRONT_Multiplayer).GetField(
            "selectedMPHeader", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static FileHeader Read(FRONT_Multiplayer receiver)
        {
            if (ReferenceEquals(receiver,null)) return null;
            if (selected == null || selected.IsStatic || selected.FieldType != typeof(FileHeader))
                throw new MissingFieldException("FRONT_Multiplayer.selectedMPHeader: FileHeader");
            return (FileHeader)selected.GetValue(receiver);
        }
    }
}
