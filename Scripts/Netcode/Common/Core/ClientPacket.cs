namespace GodotModules.Netcode
{
    public class ClientPacket : GamePacket
    {
        public ClientPacket(byte opcode, APacket writable = null)
        {
            using (var writer = new PacketWriter())
            {
                writer.Write(opcode);
                if (writable != null) writable.Write(writer);

                var stream = writer.GetStream();
                Data = stream.ToArray();
                Size = stream.Length;
            }

            Opcode = opcode;
        }
    }
}