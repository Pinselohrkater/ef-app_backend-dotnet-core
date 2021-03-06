﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Eurofurence.App.Common.ExtensionMethods;
using Eurofurence.App.Domain.Model.Abstractions;
using Eurofurence.App.Domain.Model.Announcements;
using Eurofurence.App.Domain.Model.PushNotifications;
using Eurofurence.App.Server.Services.Abstractions.PushNotifications;
using Newtonsoft.Json;

namespace Eurofurence.App.Server.Services.PushNotifications
{
    public class FirebaseChannelManager : IFirebaseChannelManager
    {
        private readonly FirebaseConfiguration _configuration;
        private readonly IEntityRepository<PushNotificationChannelRecord> _pushNotificationRepository;

        public FirebaseChannelManager(
            FirebaseConfiguration configuration,
            IEntityRepository<PushNotificationChannelRecord> pushNotificationRepository)

        {
            _configuration = configuration;
            _pushNotificationRepository = pushNotificationRepository;
        }

        private Task<IEnumerable<PushNotificationChannelRecord>> GetRecipientChannelAsync(string recipientUid)
        {
            return _pushNotificationRepository.FindAllAsync(
                a => a.Platform == PushNotificationChannelRecord.PlatformEnum.Firebase && a.Uid == recipientUid);
        }

        public Task PushAnnouncementNotificationAsync(AnnouncementRecord announcement)
        {
            return Task.WhenAll(
                SendPushNotificationAsync(new
                {
                    data = new
                    {
                        Event = "Announcement",
                        Title = announcement.Title.RemoveMarkdown(),
                        Text = announcement.Content.RemoveMarkdown(),
                        RelatedId = announcement.Id
                    },
                    to = $"/topics/{_configuration.TargetTopicAndroid}"
                }), 
                SendPushNotificationAsync(new
                {
                    data = new
                    {
                        @event = "announcement",
                        announcement_id = announcement.Id,
                    },
                    notification = new {
                        title = announcement.Title.RemoveMarkdown(),
                        body = announcement.Content.RemoveMarkdown(),
                        sound = "notification_default.caf"
                    },
                    content_available = true,
                    priority = "high",
                    to = $"/topics/{_configuration.TargetTopicIos}"
                })
            );
        }

        public async Task PushPrivateMessageNotificationAsync(string recipientUid, string toastTitle, string toastMessage, Guid relatedId)
        {
            var recipients = await GetRecipientChannelAsync(recipientUid);

            foreach (var recipient in recipients)
            {
                if (recipient.Topics.Contains("ios", StringComparer.CurrentCultureIgnoreCase))
                {
                    await SendPushNotificationAsync(new
                    {
                        data = new
                        {
                            @event = "notification",
                        },
                        notification = new
                        {
                            title = toastTitle,
                            body = toastMessage,
                            sound = "notification_default.caf"
                        },
                        to = recipient.DeviceId
                    });
                }
                else
                {
                    await SendPushNotificationAsync(new
                    {
                        data = new
                        {
                            Event = "Notification",
                            Title = toastTitle,
                            Message = toastMessage,
                            RelatedId = relatedId
                        },
                        to = recipient.DeviceId
                    });
                }
            }
        }

        public Task PushSyncRequestAsync()
        {
            return Task.WhenAll(
                SendPushNotificationAsync(new
                {
                    data = new
                    {
                        Event = "Sync",
                    },
                    to = $"/topics/{_configuration.TargetTopicAndroid}"
                }),
                SendPushNotificationAsync(new
                {
                    data = new
                    {
                        @event = "sync",
                    },
                    content_available = true,
                    priority = "high",
                    to = $"/topics/{_configuration.TargetTopicIos}"
                })
            );
        }

        private async Task SendPushNotificationAsync(object payload)
        {

            var jsonPayload = JsonConvert.SerializeObject(payload);

            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Key", $"={_configuration.AuthorizationKey}");

                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var result = await client.PostAsync("https://fcm.googleapis.com/fcm/send", content);
                result.EnsureSuccessStatusCode();
            }
        }

        public async Task RegisterDeviceAsync(string deviceId, string uid, string[] topics)
        {
            var record = (await _pushNotificationRepository.FindAllAsync(a => a.DeviceId == deviceId)).FirstOrDefault();

            var isNewRecord = record == null;

            if (isNewRecord)
            {
                record = new PushNotificationChannelRecord()
                {
                    Platform = PushNotificationChannelRecord.PlatformEnum.Firebase,
                    DeviceId = deviceId
                };
                record.NewId();
            }

            record.Touch();
            record.Uid = uid;
            record.Topics = topics.ToList();

            if (isNewRecord)
                await _pushNotificationRepository.InsertOneAsync(record);
            else
                await _pushNotificationRepository.ReplaceOneAsync(record);
        }
    }
}