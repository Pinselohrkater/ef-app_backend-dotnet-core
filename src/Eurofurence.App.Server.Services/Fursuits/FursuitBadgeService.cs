﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Eurofurence.App.Common.Utility;
using Eurofurence.App.Domain.Model.Abstractions;
using Eurofurence.App.Domain.Model.Fursuits;
using Eurofurence.App.Server.Services.Abstractions;
using Eurofurence.App.Server.Services.Abstractions.Fursuits;
using ImageSharp;
using ImageSharp.Formats;
using ImageSharp.Processing;
using SixLabors.Primitives;
using System.Threading;

namespace Eurofurence.App.Server.Services.Fursuits
{
    public class FursuitBadgeService : IFursuitBadgeService
    {
        private readonly ConventionSettings _conventionSettings;
        private readonly IEntityRepository<FursuitBadgeRecord> _fursuitBadgeRepository;
        private readonly IEntityRepository<FursuitBadgeImageRecord> _fursuitBadgeImageRepository;
        private readonly SemaphoreSlim _sync = new SemaphoreSlim(1, 1);

        public FursuitBadgeService(
            ConventionSettings conventionSettings, 
            IEntityRepository<FursuitBadgeRecord> fursuitBadgeRepository,
            IEntityRepository<FursuitBadgeImageRecord> fursuitBadgeImageRepository
            )
        {
            _conventionSettings = conventionSettings;
            _fursuitBadgeRepository = fursuitBadgeRepository;
            _fursuitBadgeImageRepository = fursuitBadgeImageRepository;
        }

        public async Task<Guid> UpsertFursuitBadgeAsync(FursuitBadgeRegistration registration)
        {
            byte[] imageBytes = Convert.FromBase64String(registration.ImageContent);
            var hash = Hashing.ComputeHashSha1(imageBytes);

            await _sync.WaitAsync();

            try
            {
                var record = await _fursuitBadgeRepository.FindOneAsync(a => a.ExternalReference == registration.BadgeNo.ToString());

                if (record == null)
                {
                    record = new FursuitBadgeRecord();
                    record.NewId();

                    await _fursuitBadgeRepository.InsertOneAsync(record);
                }

                record.ExternalReference = registration.BadgeNo.ToString();
                record.OwnerUid = $"RegSys:{_conventionSettings.ConventionNumber}:{registration.RegNo}";
                record.Gender = registration.Gender;
                record.Name = registration.Name;
                record.Species = registration.Species;
                record.IsPublic = (registration.DontPublish == 0);
                record.WornBy = registration.WornBy;
                record.Touch();

                var imageRecord = await _fursuitBadgeImageRepository.FindOneAsync(record.Id);

                if (imageRecord == null)
                {
                    imageRecord = new FursuitBadgeImageRecord
                    {
                        Id = record.Id,
                        Width = 240,
                        Height = 320,
                        MimeType = "image/jpeg"
                    };
                    imageRecord.Touch();

                    await _fursuitBadgeImageRepository.InsertOneAsync(imageRecord);
                }

                if (imageRecord.SourceContentHashSha1 != hash)
                {
                    imageRecord.SourceContentHashSha1 = hash;

                    var image = Image.Load(imageBytes);

                    image.Resize(new ResizeOptions()
                    {
                        Mode = ResizeMode.Max,
                        Size = new Size(240, 320),
                        Sampler = new BicubicResampler()
                    });

                    var ms = new MemoryStream();
                    image.SaveAsJpeg(ms, new JpegEncoderOptions() { IgnoreMetadata = true, Quality = 85 });
                    imageRecord.SizeInBytes = ms.Length;
                    imageRecord.ImageBytes = ms.ToArray();
                    ms.Dispose();

                    imageRecord.Touch();
                    await _fursuitBadgeImageRepository.ReplaceOneAsync(imageRecord);
                }

                await _fursuitBadgeRepository.ReplaceOneAsync(record);

                return record.Id;
            }
            finally
            {
                _sync.Release();
            }
        }

        public async Task<byte[]> GetFursuitBadgeImageAsync(Guid id)
        {
            var content = await _fursuitBadgeImageRepository.FindOneAsync(id);
            return content?.ImageBytes ?? null;
        }

        public Task<IEnumerable<FursuitBadgeRecord>> GetFursuitBadgesAsync()
        {
            return _fursuitBadgeRepository.FindAllAsync();
        }
    }
}
