﻿#region (c) 2010-2011 Lokad - CQRS for Windows Azure - New BSD License 

// Copyright (c) Lokad 2010-2011, http://www.lokad.com
// This code is released as Open Source under the terms of the New BSD Licence

#endregion

using System;
using System.IO;
using System.Text;

//using ProtoBuf;

namespace Lokad.Cqrs.Core.Envelope
{
	public sealed class EnvelopeStreamer : IEnvelopeStreamer
	{
		const string RefernceSignature = "[cqrs-ref-r1]";
		static readonly byte[] Reference = Encoding.Unicode.GetBytes(RefernceSignature);

		readonly IEnvelopeSerializer _envelopeSerializer;
		readonly IDataSerializer _dataSerializer;

		public EnvelopeStreamer(IEnvelopeSerializer envelopeSerializer, IDataSerializer dataSerializer)
		{
			_envelopeSerializer = envelopeSerializer;
			_dataSerializer = dataSerializer;
		}

		public byte[] SaveReferenceMessage(EnvelopeReference reference)
		{
			// important to use \r\n
			var builder = new StringBuilder();
			builder
				.Append("[cqrs-ref-r1]\r\n")
				.Append(reference.EnvelopeId).Append("\r\n")
				.Append(reference.StorageContainer).Append("\r\n")
				.Append(reference.StorageReference);

			return Encoding.Unicode.GetBytes(builder.ToString());
		}


		public byte[] SaveDataMessage(MessageEnvelope builder)
		{
			//  string contract, Guid messageId, Uri sender, 
			var itemContracts = new ItemContract[builder.Items.Length];
			using (var content = new MemoryStream())
			{
				int position = 0;
				for (int i = 0; i < builder.Items.Length; i++)
				{
					var item = builder.Items[i];

					string name;
					if (!_dataSerializer.TryGetContractNameByType(item.MappedType, out name))
					{
						var error = string.Format("Failed to find contract name for {0}", item.MappedType);
						throw new InvalidOperationException(error);
					}

					_dataSerializer.Serialize(item.Content, content);
					int size = (int) content.Position - position;
					var attribContracts = EnvelopeConvert.ItemAttributesToContract(item.GetAllAttributes());
					itemContracts[i] = new ItemContract(name, size, attribContracts);

					position += size;
				}

				var envelopeAttribs = EnvelopeConvert.EnvelopeAttributesToContract(builder.GetAllAttributes());


				var contract = new EnvelopeContract(builder.EnvelopeId, envelopeAttribs, itemContracts, builder.DeliverOn);

				using (var stream = new MemoryStream())
				{
					// skip header
					stream.Seek(MessageHeader.FixedSize, SeekOrigin.Begin);
					// save envelope attributes
					_envelopeSerializer.SerializeEnvelope(stream, contract);
					long envelopeBytes = stream.Position - MessageHeader.FixedSize;
					// copy data
					content.WriteTo(stream);
					// write the header
					stream.Seek(0, SeekOrigin.Begin);
					var header = new MessageHeader(MessageHeader.Schema2DataFormat, envelopeBytes, 0);
					header.WriteToStream(stream);
					return stream.ToArray();
				}
			}
		}

		public bool TryReadAsReference(byte[] buffer, out EnvelopeReference reference)
		{
			if (BytesStart(buffer, Reference))
			{
				string text = Encoding.Unicode.GetString(buffer);
				string[] args = text.Split(new[] {"\r\n"}, StringSplitOptions.RemoveEmptyEntries);
				reference = new EnvelopeReference(args[1], args[2], args[3]);
				return true;
			}
			reference = null;
			return false;
		}

		static bool BytesStart(byte[] buffer, byte[] signature)
		{
			if (buffer.Length < signature.Length)
				return false;

			for (int i = 0; i < signature.Length; i++)
			{
				if (buffer[i] != signature[i])
					return false;
			}

			return true;
		}


		public MessageEnvelope ReadDataMessage(byte[] buffer)
		{
			var header = MessageHeader.ReadHeader(buffer);


			if (header.MessageFormatVersion != MessageHeader.Schema2DataFormat)
				throw new InvalidOperationException("Unexpected message format");


			EnvelopeContract envelope;
			using (var stream = new MemoryStream(buffer, MessageHeader.FixedSize, (int) header.EnvelopeBytes))
			{
				envelope = _envelopeSerializer.DeserializeEnvelope(stream);
			}
			int index = MessageHeader.FixedSize + (int) header.EnvelopeBytes;
			//var count = (int)header.ContentLength;

			var items = new MessageItem[envelope.Items.Length];

			for (int i = 0; i < items.Length; i++)
			{
				var itemContract = envelope.Items[i];
				var attributes = EnvelopeConvert.AttributesFromContract(itemContract.Attributes);
				Type contractType;
				if (_dataSerializer.TryGetContractTypeByName(itemContract.ContractName, out contractType))
				{
					using (var stream = new MemoryStream(buffer, index, itemContract.ContentSize))
					{
						object instance = _dataSerializer.Deserialize(stream, contractType);

						items[i] = new MessageItem(contractType, instance, attributes);
					}
				}
				else
				{
					// we can't deserialize. Keep it as buffer
					var bufferInstance = new byte[itemContract.ContentSize];
					Buffer.BlockCopy(buffer, index, bufferInstance, 0, itemContract.ContentSize);
					items[i] = new MessageItem(null, bufferInstance, attributes);
				}

				index += itemContract.ContentSize;
			}

			var envelopeAttributes = EnvelopeConvert.AttributesFromContract(envelope.EnvelopeAttributes);
			return new MessageEnvelope(envelope.EnvelopeId, envelopeAttributes, items, envelope.DeliverOnUtc);
		}
	}
}