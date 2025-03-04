// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages
{
    public class StatusMessageSerializer : IZeroInnerMessageSerializer<StatusMessage>
    {
        public void Serialize(IByteBuffer byteBuffer, StatusMessage message)
        {
            int forkIdContentLength = 0;

            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                forkIdContentLength = Rlp.LengthOf(forkId.ForkHash) + Rlp.LengthOf(forkId.Next);
            }

            NettyRlpStream rlpStream = new(byteBuffer);
            int totalLength = GetLength(message, out int contentLength);
            byteBuffer.EnsureWritable(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(message.ProtocolVersion);
            rlpStream.Encode(message.ChainId);
            rlpStream.Encode(message.TotalDifficulty);
            rlpStream.Encode(message.BestHash);
            rlpStream.Encode(message.GenesisHash);
            if (message.ForkId is not null)
            {
                ForkId forkId = message.ForkId.Value;
                rlpStream.StartSequence(forkIdContentLength);
                rlpStream.Encode(forkId.ForkHash);
                rlpStream.Encode(forkId.Next);
            }
        }

        public int GetLength(StatusMessage message, out int contentLength)
        {

            int forkIdSequenceLength = 0;
            if (message.ForkId.HasValue)
            {
                ForkId forkId = message.ForkId.Value;
                int forkIdContentLength = Rlp.LengthOf(forkId.ForkHash) + Rlp.LengthOf(forkId.Next);
                forkIdSequenceLength = Rlp.LengthOfSequence(forkIdContentLength);
            }

            contentLength =
                Rlp.LengthOf(message.ProtocolVersion) +
                Rlp.LengthOf(message.ChainId) +
                Rlp.LengthOf(message.TotalDifficulty) +
                Rlp.LengthOf(message.BestHash) +
                Rlp.LengthOf(message.GenesisHash) +
                forkIdSequenceLength;

            return Rlp.LengthOfSequence(contentLength);
        }

        public StatusMessage Deserialize(IByteBuffer byteBuffer)
        {
            RlpStream rlpStream = new NettyRlpStream(byteBuffer);
            return Deserialize(rlpStream);
        }

        private static StatusMessage Deserialize(RlpStream rlpStream)
        {
            StatusMessage statusMessage = new();
            rlpStream.ReadSequenceLength();
            statusMessage.ProtocolVersion = rlpStream.DecodeByte();
            statusMessage.ChainId = rlpStream.DecodeUInt256();
            statusMessage.TotalDifficulty = rlpStream.DecodeUInt256();
            statusMessage.BestHash = rlpStream.DecodeKeccak();
            statusMessage.GenesisHash = rlpStream.DecodeKeccak();
            if (rlpStream.Position < rlpStream.Length)
            {
                rlpStream.ReadSequenceLength();
                byte[] forkHash = rlpStream.DecodeByteArray();
                ulong next = rlpStream.DecodeUlong();
                ForkId forkId = new(forkHash, next);
                statusMessage.ForkId = forkId;
            }

            return statusMessage;
        }
    }
}
