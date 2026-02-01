namespace ZScape.Protocol;

/// <summary>
/// Huffman codec for Zandronum protocol encoding/decoding.
/// Based on the Skulltag Launcher Protocol implementation by Timothy Landers.
/// </summary>
public class HuffmanCodec
{
    private static HuffmanCodec? _instance;
    private static readonly object _lock = new();

    private readonly HuffmanNode _root;
    private readonly HuffmanNode?[] _codeTable;
    private readonly bool _reverseBits = true;
    private readonly bool _allowExpansion = false;

    /// <summary>
    /// Bit reverse lookup table for backwards compatibility with original Huffman encoding.
    /// </summary>
    private static readonly byte[] ReverseMap =
    [
        0,128, 64,192, 32,160, 96,224, 16,144, 80,208, 48,176,112,240,
        8,136, 72,200, 40,168,104,232, 24,152, 88,216, 56,184,120,248,
        4,132, 68,196, 36,164,100,228, 20,148, 84,212, 52,180,116,244,
        12,140, 76,204, 44,172,108,236, 28,156, 92,220, 60,188,124,252,
        2,130, 66,194, 34,162, 98,226, 18,146, 82,210, 50,178,114,242,
        10,138, 74,202, 42,170,106,234, 26,154, 90,218, 58,186,122,250,
        6,134, 70,198, 38,166,102,230, 22,150, 86,214, 54,182,118,246,
        14,142, 78,206, 46,174,110,238, 30,158, 94,222, 62,190,126,254,
        1,129, 65,193, 33,161, 97,225, 17,145, 81,209, 49,177,113,241,
        9,137, 73,201, 41,169,105,233, 25,153, 89,217, 57,185,121,249,
        5,133, 69,197, 37,165,101,229, 21,149, 85,213, 53,181,117,245,
        13,141, 77,205, 45,173,109,237, 29,157, 93,221, 61,189,125,253,
        3,131, 67,195, 35,163, 99,227, 19,147, 83,211, 51,179,115,243,
        11,139, 75,203, 43,171,107,235, 27,155, 91,219, 59,187,123,251,
        7,135, 71,199, 39,167,103,231, 23,151, 87,215, 55,183,119,247,
        15,143, 79,207, 47,175,111,239, 31,159, 95,223, 63,191,127,255
    ];

    /// <summary>
    /// The Zandronum/Skulltag compatible Huffman tree structure.
    /// </summary>
    private static readonly byte[] CompatibleHuffmanTree =
    [
        0,  0,  0,  1,128,  0,  0,  0,  3, 38, 34,  2,  1, 80,  3,110,
        144, 67,  0,  2,  1, 74,  3,243,142, 37,  2,  3,124, 58,182,  0,
        0,  1, 36,  0,  3,221,131,  3,245,163,  1, 35,  3,113, 85,  0,
        1, 41,  1, 77,  3,199,130,  0,  1,206,  3,185,153,  3, 70,118,
        0,  3,  3,  5,  0,  0,  1, 24,  0,  2,  3,198,190, 63,  2,  3,
        139,186, 75,  0,  1, 44,  2,  3,240,218, 56,  3, 40, 39,  0,  0,
        2,  2,  3,244,247, 81, 65,  0,  3,  9,125,  3, 68, 60,  0,  0,
        1, 25,  3,191,138,  3, 86, 17,  0,  1, 23,  3,220,178,  2,  3,
        165,194, 14,  1,  0,  2,  2,  0,  0,  2,  1,208,  3,150,157,181,
        1,222,  2,  3,216,230,211,  0,  2,  2,  3,252,141, 10, 42,  0,
        2,  3,134,135,104,  1,103,  3,187,225, 95, 32,  0,  0,  0,  0,
        0,  0,  1, 57,  1, 61,  3,183,237,  0,  0,  3,233,234,  3,246,
        203,  2,  3,250,147, 79,  1,129,  0,  1,  7,  3,143,136,  1, 20,
        3,179,148,  0,  0,  0,  3, 28,106,  3,101, 87,  1, 66,  0,  3,
        180,219,  3,227,241,  0,  1, 26,  1,251,  3,229,214,  3, 54, 69,
        0,  0,  0,  0,  0,  3,231,212,  3,156,176,  3, 93, 83,  0,  3,
        96,253,  3, 30, 13,  0,  0,  2,  3,175,254, 94,  3,159, 27,  2,
        1,  8,  3,204,226, 78,  0,  0,  0,  3,107, 88,  1, 31,  3,137,
        169,  2,  2,  3,215,145,  6,  4,  1,127,  0,  1, 99,  3,209,217,
        0,  3,213,238,  3,177,170,  1,132,  0,  0,  0,  2,  3, 22, 12,
        114,  2,  2,  3,158,197, 97, 45,  0,  1, 46,  1,112,  3,174,249,
        0,  3,224,102,  2,  3,171,151,193,  0,  0,  0,  3, 15, 16,  3,
        2,168,  1, 49,  3, 91,146,  0,  1, 48,  3,173, 29,  0,  3, 19,
        126,  3, 92,242,  0,  0,  0,  0,  0,  0,  3,205,192,  2,  3,235,
        149,255,  2,  3,223,184,248,  0,  0,  3,108,236,  3,111, 90,  2,
        3,117,115, 71,  0,  0,  3, 11, 50,  0,  3,188,119,  1,122,  3,
        167,162,  1,160,  1,133,  3,123, 21,  0,  0,  2,  1, 59,  2,  3,
        155,154, 98, 43,  0,  3, 76, 51,  2,  3,201,116, 72,  2,  0,  2,
        3,109,100,121,  2,  3,195,232, 18,  1,  0,  2,  0,  1,164,  2,
        3,120,189, 73,  0,  1,196,  3,239,210,  3, 64, 62, 89,  0,  0,
        1, 33,  2,  3,228,161, 55,  2,  3, 84,152, 47,  0,  0,  2,  3,
        207,172,140,  3, 82,166,  0,  3, 53,105,  1, 52,  3,202,200
    ];

    private HuffmanCodec()
    {
        _codeTable = new HuffmanNode?[256];
        _root = new HuffmanNode { BitCount = 0, Code = 0, Value = -1 };
        BuildTree(_root, CompatibleHuffmanTree, 0, CompatibleHuffmanTree.Length, _codeTable, 256);
    }

    /// <summary>
    /// Gets the singleton instance of the Huffman codec.
    /// </summary>
    public static HuffmanCodec Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new HuffmanCodec();
                }
            }
            return _instance;
        }
    }

    /// <summary>
    /// Encodes data using Huffman encoding.
    /// </summary>
    /// <param name="input">The data to encode.</param>
    /// <returns>The encoded data, or null if encoding fails.</returns>
    public byte[]? Encode(byte[] input)
    {
        if (input == null || input.Length == 0)
            return null;

        int maxOutputSize = _allowExpansion ? input.Length * 3 : input.Length + 1;
        byte[] output = new byte[maxOutputSize];

        // Reserve space for padding byte
        int bitPosition = 8;

        foreach (byte b in input)
        {
            var node = _codeTable[b];
            if (node == null)
                return null;

            // Write the Huffman code bit by bit
            for (int i = node.BitCount - 1; i >= 0; i--)
            {
                int bit = (node.Code >> i) & 1;
                if (bit == 1)
                {
                    int byteIndex = bitPosition / 8;
                    int bitIndex = 7 - (bitPosition % 8);
                    if (byteIndex >= output.Length)
                        return null; // Buffer overflow
                    output[byteIndex] |= (byte)(1 << bitIndex);
                }
                bitPosition++;

                if (bitPosition / 8 >= maxOutputSize && !_allowExpansion)
                    return null; // Would expand data
            }
        }

        // Calculate padding
        int bytesWritten = (bitPosition + 7) / 8;
        int padding = (8 - (bitPosition % 8)) % 8;
        output[0] = (byte)padding;

        // Reverse bits if needed for compatibility
        if (_reverseBits)
        {
            for (int i = 1; i < bytesWritten; i++)
            {
                output[i] = ReverseMap[output[i]];
            }
        }

        // Resize to actual size
        Array.Resize(ref output, bytesWritten);
        return output;
    }

    /// <summary>
    /// Decodes Huffman-encoded data.
    /// </summary>
    /// <param name="input">The encoded data.</param>
    /// <returns>The decoded data, or null if decoding fails.</returns>
    public byte[]? Decode(byte[] input)
    {
        if (input == null || input.Length == 0)
            return null;

        // Check for unencoded signal (0xFF prefix)
        if (input[0] == 0xFF)
        {
            byte[] result = new byte[input.Length - 1];
            Array.Copy(input, 1, result, 0, input.Length - 1);
            return result;
        }

        int padding = input[0];
        int bitsAvailable = ((input.Length - 1) * 8) - padding;
        
        List<byte> output = new();
        HuffmanNode? node = _root;
        int bitIndex = 8; // Start after padding byte

        while (bitsAvailable > 0 && node != null)
        {
            int byteIndex = bitIndex / 8;
            if (byteIndex >= input.Length)
                break;

            byte currentByte = input[byteIndex];
            if (_reverseBits)
                currentByte = ReverseMap[currentByte];

            int bitOffset = 7 - (bitIndex % 8);
            int bit = (currentByte >> bitOffset) & 1;

            // Traverse the tree
            if (node.Left != null && node.Right != null)
            {
                node = bit == 0 ? node.Left : node.Right;
            }

            // Check if we've reached a leaf node
            if (node != null && node.Left == null && node.Right == null)
            {
                output.Add((byte)node.Value);
                node = _root; // Reset to root for next character
            }

            bitIndex++;
            bitsAvailable--;
        }

        return [.. output];
    }

    private int BuildTree(HuffmanNode node, byte[] treeData, int index, int dataLength, 
                          HuffmanNode?[] codeTable, int tableLength)
    {
        if (index >= dataLength)
            return -1;

        int desc = treeData[index];
        index++;

        // Create child nodes
        node.Left = new HuffmanNode();
        node.Right = new HuffmanNode();

        // Process left (0) and right (1) children
        for (int i = 0; i < 2; i++)
        {
            var child = i == 0 ? node.Left : node.Right;
            child.BitCount = node.BitCount + 1;
            child.Code = (node.Code << 1) | i;
            child.Value = -1;

            // Check if this child is a branch or leaf
            if ((desc & (1 << i)) == 0)
            {
                // Branch node - recurse
                index = BuildTree(child, treeData, index, dataLength, codeTable, tableLength);
                if (index < 0)
                    return -1;
            }
            else
            {
                // Leaf node
                if (index >= dataLength)
                    return -1;

                child.Value = treeData[index] & 0xFF;
                child.Left = null;
                child.Right = null;

                // Store in code table
                if (child.Value >= 0 && child.Value < tableLength)
                {
                    codeTable[child.Value] = child;
                }
                index++;
            }
        }

        return index;
    }

    private class HuffmanNode
    {
        public int BitCount { get; set; }
        public int Code { get; set; }
        public int Value { get; set; }
        public HuffmanNode? Left { get; set; }
        public HuffmanNode? Right { get; set; }
    }
}
