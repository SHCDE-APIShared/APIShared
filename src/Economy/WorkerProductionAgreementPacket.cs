using System;
using MessagePack;
using MessagePack.Formatters;

namespace APIShared.Economy
{
    [MessagePackFormatter(typeof(WorkerProductionAgreementPacketFormatter))]
    internal sealed class WorkerProductionAgreementPacket
    {
        internal const int CurrentVersion=1;
        internal int Version=CurrentVersion,Operation;
        internal ulong Lobby;
        internal string Roster,Token;
        internal WorkerProductionAgreementDescriptor Descriptor;
    }
    internal sealed class WorkerProductionAgreementPacketFormatter:IMessagePackFormatter<WorkerProductionAgreementPacket>
    {
        public WorkerProductionAgreementPacketFormatter() { }
        public void Serialize(ref MessagePackWriter writer,WorkerProductionAgreementPacket value,MessagePackSerializerOptions options)
        {
            if(value==null){writer.WriteNil();return;}
            writer.WriteArrayHeader(16);writer.Write(value.Version);writer.Write(value.Operation);writer.Write(value.Lobby);
            writer.Write(value.Roster);writer.Write(value.Token);var d=value.Descriptor;
            writer.Write(d.ServiceKey);writer.Write(d.RulesDigest);writer.Write(d.WireMode);writer.Write(d.SidecarKey);
            writer.Write(d.ReadableSidecarSchema);writer.Write(d.RequiredSidecarSchema);writer.Write(d.CanExecute);
            writer.Write(d.HasDemand);writer.Write(d.RequiredDataValid);writer.Write(d.SavedRecordDigest);writer.Write(0);
        }
        public WorkerProductionAgreementPacket Deserialize(ref MessagePackReader reader,MessagePackSerializerOptions options)
        {
            if(reader.TryReadNil())return null;
            int count=reader.ReadArrayHeader();if(count<1)throw new InvalidOperationException("Empty production agreement.");
            int version=reader.ReadInt32();
            if(version!=WorkerProductionAgreementPacket.CurrentVersion || count!=16)
            {for(int i=1;i<count;i++)reader.Skip();return new WorkerProductionAgreementPacket{Version=version,Operation=-1};}
            var p=new WorkerProductionAgreementPacket{Version=version,Operation=reader.ReadInt32(),Lobby=reader.ReadUInt64(),
                Roster=Text(ref reader,512),Token=Text(ref reader,32)};
            string key=Text(ref reader,256),digest=Text(ref reader,128),wire=Text(ref reader,128),sidecar=Text(ref reader,256);
            int readable=reader.ReadInt32(),required=reader.ReadInt32();bool executable=reader.ReadBoolean(),demand=reader.ReadBoolean(),valid=reader.ReadBoolean();
            string saved=Text(ref reader,128);reader.Skip();
            if(p.Operation<0 || p.Operation>3 || readable<0 || required<0)throw new InvalidOperationException("Invalid production agreement fields.");
            p.Descriptor=new WorkerProductionAgreementDescriptor(key,digest,wire,sidecar,readable,required,executable,demand,valid,saved);return p;
        }
        private static string Text(ref MessagePackReader reader,int maximum)
        {string value=reader.ReadString();if(value==null || value.Length>maximum)throw new InvalidOperationException("Invalid production agreement text.");return value;}
    }
}
