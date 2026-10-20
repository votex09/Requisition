namespace TerraStorage.Common
{
    // How many elements a packet says are coming. Sizing a list from that number before checking it
    // hands a modified client a one-packet way to make the server allocate gigabytes: List<T>(capacity)
    // commits the whole backing array before the first element is read, so no amount of care in the
    // read loop can bound it. Nor does the loop bound itself - Terraria reads every packet into one
    // reused buffer, so reading past the packet returns the previous packet's bytes rather than
    // throwing. The rule lives here, away from the netcode, so it can be exercised without Terraria.
    public static class WireCount
    {
        // Terraria.MessageBuffer.readBufferMax - the single buffer every packet is read into, and
        // therefore the most bytes any one handler can ever see. tModLoader refuses to send a packet
        // over 65,535 bytes (Terraria.ModLoader.ModPacket.Send), so this is already generous.
        public const int LargestPacketBytes = 131070;

        // Guid.ToByteArray
        public const int GuidBytes = 16;

        // Whether a count could honestly describe elements that fit in one packet. For a list whose
        // length the game itself does not constrain, this is the only bound there is.
        public static bool FitsInOnePacket(int count, int bytesPerElement)
        {
            if (count < 0)
                return false;

            if (bytesPerElement <= 0)
                return false;

            int largestHonestCount = LargestPacketBytes / bytesPerElement;
            return count <= largestHonestCount;
        }

        // Whether a count could honestly describe the stacks on a disk. A disk holds at most its
        // tier's capacity, which is a far tighter bound than the packet size and needs no constant
        // of its own.
        public static bool FitsDiskCapacity(int count, int capacity)
        {
            if (count < 0)
                return false;

            if (capacity < 0)
                return false;

            return count <= capacity;
        }
    }
}
