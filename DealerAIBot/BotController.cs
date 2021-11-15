// <copyright file="BotController.cs" company="Idea Notion Development Inc">
// Copyright (c) Idea Notion Development Inc. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Bot.Core.DTOs.Conversation;
using Bot.Core.Extension;
using Bot.Core.Model;
using Bot.Core.Model.Blob;
using Bot.Core.Model.Entity;
using Bot.Core.Model.Xml;
using Bot.Core.Service;
using Bot.Core.Service.Analytics;
using Bot.Core.Service.Conversation;
using Bot.Core.Service.Email;
using Bot.Core.Service.Messaging;
using Bot.Core.Service.Notification;
using Bot.Web.Attributes;
using Bot.Web.DTOs;
using Bot.Web.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Azure.Documents.Linq;
using Microsoft.Azure.Documents.SystemFunctions;
using Microsoft.Azure.NotificationHubs;
using Microsoft.Bot.Connector;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using TimeZoneConverter;

namespace Bot.Web.Controllers
{
	[ApiExceptionFilter]
	[Produces("application/json")]
	[Route(template: "api/bot")]
	public class BotController : Controller
	{
		private readonly IConfiguration _config;
		private readonly BotDbContext _context;
		private readonly IConversationService _conversationService;
		private readonly IMessageService _messageService;
		private readonly EmailService _emailService;
		private readonly BotUserService _botUserService;
		private readonly DealershipService _dealershipService;
		private readonly AnalyticsService _analyticsService;
		private readonly AppointmentService _appointmentService;
		private readonly NotificationService _notificationService;
		private readonly NotificationHubClient _notificationHubClient;
		private readonly string _bccEmail;

		public BotController(IConfiguration configuration, BotDbContext context, IConversationService conversationService, EmailService emailService, BotUserService botUserService, DealershipService dealershipService, AnalyticsService analyticsService, AppointmentService appointmentService, IMessageService messageService, NotificationService notificationService, NotificationHubClient notificationHubClient)
		{
			_config = configuration;
			_context = context;
			_conversationService = conversationService;
			_messageService = messageService;
			_emailService = emailService;
			_botUserService = botUserService;
			_dealershipService = dealershipService;
			_analyticsService = analyticsService;
			_appointmentService = appointmentService;
			_notificationService = notificationService;
			_notificationHubClient = notificationHubClient;
			_bccEmail = configuration["EmailSettings:GmailFromEmail"];
		}

		[HttpGet]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		public dynamic GetBot(string dealerId)
		{
			var dealer = _context.Dealers.Include(t => t.FacebookPage).First(t => t.Id == dealerId);

			var json = new DTOs.Bot { Id = dealer.Id, ManufacturerId = dealer.ManufacturerId, Active = dealer.BotActive, Name = dealer.BotName, DirectChatKey = dealer.DirectChatKey, AutoOnOffSchedule = dealer.AutoOnOffSchedule };
			if (dealer.FacebookPage != null)
			{
				json.FacebookPage = new DTOs.Bot.Facebook { PageId = dealer.FacebookPage.PageId, PageName = dealer.FacebookPage.PageName };
			}

			return Ok(json);
		}

		[HttpPost]
		[CustomAuthorize("DealerAdmin")]
		[ClaimsFilter]
		[Route(template: "facebook")]
		public dynamic SetBotFacebook(string dealerId, [FromBody] DTOs.Bot.Facebook body)
		{
			if (body.AccessToken == null || body.PageId == null)
			{
				throw new ArgumentException("invalid post data");
			}

			var dealer = _context.Dealers.Include(t => t.FacebookPage).First(t => t.Id == dealerId);
			if (dealer.FacebookPage == null)
			{
				dealer.FacebookPage = new FacebookPage
				{
					CreatedDate = DateTime.UtcNow,
				};
			}

			dealer.FacebookPage.PageId = body.PageId;
			dealer.FacebookPage.PageName = body.PageName;
			dealer.FacebookPage.PageAccessToken = body.AccessToken;
			dealer.FacebookPage.LastModifiedDate = DateTime.UtcNow;

			_context.SaveChanges();

			return Ok();
		}

		[HttpDelete]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route(template: "facebook")]
		public dynamic DeleteBotFacebook(string dealerId)
		{
			var dealer = _context.Dealers.Include(t => t.FacebookPage).First(t => t.Id == dealerId);
			var page = dealer.FacebookPage;
			if (page != null)
			{
				dealer.FacebookPageId = null;
				_context.FacebookPages.Remove(page);
				_context.SaveChanges();
			}

			return Ok();
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		public dynamic UpdateBot(string dealerId, [FromBody] DTOs.Bot bot)
		{
			var dealer = _context.Dealers.First(t => t.Id == dealerId);
			dealer.BotActive = bot.Active;

			_context.SaveChanges();
			return Ok();
		}

		[HttpPut]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("autoOffSchedule")]
		public async Task<dynamic> UpdateBotSchedule(string dealerId, [FromBody] bool autoOffSchedule)
		{
			var dealer = _context.Dealers.First(t => t.Id == dealerId);
			dealer.AutoOnOffSchedule = autoOffSchedule;
			await _context.SaveChangesAsync();
			return Ok();
		}

		[HttpGet]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users")]
		public dynamic GetUsers(string dealerId)
		{
			var users = _botUserService.GetBotUsers(dealerId).Where(u => u.LastMessage != null && (!u.IsArchived.IsDefined() || !u.IsArchived))
				.OrderByDescending(t => t.LastModifiedDate).ToList();

			return Ok(users);
		}

		[HttpGet]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/{filter}/chats")]
		public async Task<dynamic> ChatsWithFilter(string dealerId, string filter, [FromQuery] string botUserId)
		{
			var users = new List<Core.Model.Document.BotUser>();
			switch (filter)
			{
				case "All":
					users = (await _botUserService.GetBotUsers(dealerId).Where(u => u.LastMessage != null && (!u.IsArchived.IsDefined() || !u.IsArchived))
					.OrderByDescending(t => t.LastModifiedDate).AsDocumentQuery().ExecuteNextAsync<Core.Model.Document.BotUser>()).ToList();
					break;
				case "Unread":
					users = (await _botUserService.GetBotUsers(dealerId).Where(u => u.LastMessage != null && (!u.IsArchived.IsDefined() || !u.IsArchived) && u.Unread > 0)
					.OrderByDescending(t => t.LastModifiedDate).AsDocumentQuery().ExecuteNextAsync<Core.Model.Document.BotUser>()).ToList();
					break;
				case "Read":
					users = (await _botUserService.GetBotUsers(dealerId).Where(u => u.LastMessage != null && (!u.IsArchived.IsDefined() || !u.IsArchived) && u.Unread == 0)
					.OrderByDescending(t => t.LastModifiedDate).AsDocumentQuery().ExecuteNextAsync<Core.Model.Document.BotUser>()).ToList();
					break;
				case "Deleted":
					users = (await _botUserService.GetBotUsers(dealerId).Where(u => u.LastMessage != null && u.IsArchived.IsDefined() && u.IsArchived)
					.OrderByDescending(t => t.LastModifiedDate).AsDocumentQuery().ExecuteNextAsync<Core.Model.Document.BotUser>()).ToList();
					break;
				default:
					break;
			}

			if (!string.IsNullOrEmpty(botUserId) && (string.Equals(filter, "All") || string.Equals(filter, "Read")))
			{
				var searchUserIndex = users.FindIndex(u => u.Id == botUserId);

				if (searchUserIndex >= 0)
				{
					var searchUser = users[searchUserIndex];
					for (var i = searchUserIndex; i > 0; i--)
					{
						users[i] = users[i - 1];
					}

					users[0] = searchUser;
				}
				else
				{
					var searchUser = _botUserService.GetBotUser(dealerId, botUserId);
					if (searchUser != null && searchUser.LastMessage != null && !searchUser.IsArchived)
					{
						users.RemoveAt(users.Count - 1);
						users.Insert(0, searchUser);
					}
				}
			}

			return Ok(users);
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/{botUserId}/read")]
		public async Task<dynamic> MarkRead(string dealerId, string botUserId)
		{
			await _botUserService.MarkAsRead(dealerId, botUserId);
			await _notificationService.SendHubAsync(dealerId, "onUpdateUnread");

			return Ok();
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/archive")]
		public async Task<dynamic> ArchiveUser(string dealerId, [FromBody] string[] botUserId)
		{
			for (var x = 0; x < botUserId.Length; x++)
			{
				var botUser = _botUserService.GetBotUser(dealerId, botUserId[x]);
				if (botUser == null)
				{
					return NotFound();
				}

				botUser.IsArchived = true;
				await _botUserService.UpsertAsync(botUser);
			}

			await _notificationService.SendHubAsync(dealerId, "onArchiveBotUsers", botUserId);

			return Ok();
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/{botUserId}/takeover")]
		public async Task<IActionResult> TakeOver(string dealerId, string botUserId)
		{
			var botUser = _botUserService.GetBotUser(dealerId, botUserId);
			botUser.InHandover = true;
			await _botUserService.UpsertAsync(botUser);

			await _notificationService.SendHubAsync(dealerId, "onTakeOver", botUserId);

			return Ok();
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/{botUserId}/passback")]
		public async Task<IActionResult> PassBack(string dealerId, string botUserId)
		{
			var botUser = _botUserService.GetBotUser(dealerId, botUserId);
			botUser.InHandover = false;
			await _botUserService.UpsertAsync(botUser);

			await _notificationService.SendHubAsync(dealerId, "onPassBack", botUserId);

			return Ok();
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/takeover")]
		public async Task<IActionResult> TakeOverGroup(string dealerId, [FromBody] string[] botUserId)
		{
			for (var i = 0; i < botUserId.Length; i++)
			{
				var botUser = _botUserService.GetBotUser(dealerId, botUserId[i]);
				botUser.InHandover = true;
				await _botUserService.UpsertAsync(botUser);
			}

			return Ok();
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		[Route("users/passback")]
		public async Task<IActionResult> PassBackGroup(string dealerId, [FromBody] string[] botUserId)
		{
			for (var i = 0; i < botUserId.Length; i++)
			{
				var botUser = _botUserService.GetBotUser(dealerId, botUserId[i]);
				botUser.InHandover = false;
				await _botUserService.UpsertAsync(botUser);
			}

			return Ok();
		}

		[HttpGet("conversation/{conversationId}")]
		public async Task<Conversation> GetConversationAsync(string conversationId)
		{
			return await _conversationService.GetConversationAsync(conversationId);
		}

		[HttpGet("conversation/{conversationId}/messages")]
		public async Task<Page<Message>> GetMessagesAsync(string conversationId, [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] bool reverse)
		{
			return await _conversationService.GetMessagesAsync(conversationId, page, pageSize, reverse);
		}

		[HttpPost("conversation/{conversationId}/outbox")]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		public async Task SendMessageAsync(string dealerId, string conversationId, [FromBody] Message message)
		{
			var conversation = await _conversationService.GetConversationAsync(conversationId);
			if (conversation != null)
			{
				await _messageService.SendMessageToUserAsync(conversation, message, dealerId);
				await _notificationService.SendChatAsync(conversation.Id, "onReceiveMessage", message);
			}
		}

		[HttpPost("conversation/{conversationId}/inbox")]
		public async Task ReceiveMessageAsync(string conversationId, [FromBody] Message message, string dealerId = null)
		{
			var conversation = await _conversationService.GetConversationAsync(conversationId);

			if (conversation == null)
			{
				return;
			}

			await _notificationService.SendChatAsync(conversation.Id, "onReceiveMessage", message);

			if (!string.IsNullOrEmpty(dealerId))
			{
				await _notificationService.SendHubAsync(dealerId, "onUpdateUnread");
				await _notificationService.SendHubAsync(dealerId, "onUpdateMessageAction", string.Empty, message);
			}

			var botUser = _botUserService.GetBotUser(dealerId, conversation.User.Id, false);

			if (message.IsFromUser)
			{
				var dealer = _dealershipService.GetDealer(dealerId);

				if (botUser.LastEmailNotificationDateTime == null || (message.DateCreated - botUser.LastEmailNotificationDateTime.Value).TotalMinutes >= 60)
				{
					var messageDateTimeLocal = System.TimeZoneInfo.ConvertTime(message.DateCreated, TZConvert.GetTimeZoneInfo(dealer.TimeZone ?? "Eastern Standard Time"));

					var parameters = new Dictionary<string, string>
					{
						["from"] = message.Name,
						["messageText"] = message.Text,
						["messageDateTime"] = messageDateTimeLocal.ToString("HH:mm:ss"),
						["dealerName"] = dealer.Name,
						["dealerAddress"] = dealer.Location?.AddressDisplay,
					};

					if (dealer.PortalChatSettings != null && dealer.PortalChatSettings.ReceiveNotifications != null)
					{
						if (dealer.PortalChatSettings.ReceiveNotifications == true)
						{
							if (!string.IsNullOrWhiteSpace(dealer.PortalChatSettings.Email))
							{
								await _emailService.SendEmailAsync("RECEIVE_MESSAGE", dealerId, dealer.PortalChatSettings.Email, null, null, null, parameters, null).ExecuteWithRetryAsync();
							}

							await SendApplePushNotificationMessageAsync(dealerId, dealer.Name, $"{message.Name}\n{message.Text}");
						}
					}

					botUser.LastEmailNotificationDateTime = DateTime.UtcNow;
					await _botUserService.UpsertAsync(botUser);
				}
			}
		}

		[HttpPost("conversation/{conversationId}/typing/{lastTimeTyped}")]
		public async Task ReceiveTypingMessageAsync(string conversationId, long lastTimeTyped)
		{
			await _notificationService.SendChatAsync(conversationId, "onReceiveTypingMessage", lastTimeTyped);
		}

		[HttpPost]
		[Route("email/send/appointment")]
		public async Task<IActionResult> SendAppointmentEmail([FromBody] AppointmentEmail appointmentEmail)
		{
			var dealership = _dealershipService.GetDealer(appointmentEmail.DealerId);

			string timeParam = string.Empty;

			if (DateTime.TryParse(appointmentEmail.Time, out DateTime t))
			{
				timeParam = DateTime.Parse(appointmentEmail.Time).ToString("hh:mm tt");
			}

			// common email parameters
			var parameters = new Dictionary<string, string>
			{
				{ "month", appointmentEmail.Date.ToString("MMM") },
				{ "day", appointmentEmail.Date.ToString("dd") },
				{ "time", timeParam },
				{ "name", appointmentEmail.Name },
				{ "dealer_name", dealership.Name },
				{ "dealer_address", dealership.Location?.AddressDisplay },
			};

			if (!string.IsNullOrEmpty(appointmentEmail.PhoneNum))
			{
				parameters.Add("phone_display", "block");
				parameters.Add("phone_num", appointmentEmail.PhoneNum);
				parameters.Add("contact", appointmentEmail.PhoneNum);
			}
			else
			{
				parameters.Add("phone_display", "none");
			}

			if (!string.IsNullOrEmpty(appointmentEmail.Email))
			{
				parameters.Add("email_display", "block");
				parameters.Add("email", appointmentEmail.Email);
				if (!parameters.ContainsKey("contact"))
				{
					parameters.Add("contact", appointmentEmail.Email);
				}
			}
			else
			{
				parameters.Add("email_display", "none");
			}

			if (!string.IsNullOrEmpty(appointmentEmail.ExtraInfo))
			{
				parameters.Add("extra_display", "block");
				parameters.Add("extra_info", appointmentEmail.ExtraInfo);
			}
			else
			{
				parameters.Add("extra_display", "none");
			}

			if (string.IsNullOrEmpty(appointmentEmail.PhoneNum) && string.IsNullOrEmpty(appointmentEmail.Email))
			{
				parameters.Add("contact", string.Empty);
			}

			// additional parameters and key
			var emailTypeKey = string.Empty;

			if (appointmentEmail.AppointmentType == AppointmentType.LeadGen)
			{
				emailTypeKey = "LEAD_GEN";
				parameters.Add("channel", appointmentEmail.Channel);

				if (!string.IsNullOrEmpty(appointmentEmail.Reason))
				{
					parameters.Add("reason_display", "block");
					parameters.Add("reason", appointmentEmail.Reason);
				}
				else
				{
					parameters.Add("reason_display", "none");
				}
			}
			else if (appointmentEmail.AppointmentType == AppointmentType.Sales)
			{
				emailTypeKey = "SALES_APPOINTMENT_DEALER";
			}
			else if (appointmentEmail.AppointmentType == AppointmentType.Service)
			{
				emailTypeKey = "SERVICE_APPOINTMENT_DEALER";

				if (!string.IsNullOrEmpty(appointmentEmail.Vehicle))
				{
					parameters.Add("vehicle_display", "block");
					parameters.Add("vehicle", appointmentEmail.Vehicle);
				}
				else
				{
					parameters.Add("vehicle_display", "none");
				}

				if (!string.IsNullOrEmpty(appointmentEmail.ServiceRequested))
				{
					parameters.Add("service_display", "block");
					parameters.Add("service_requested", appointmentEmail.ServiceRequested);
				}
				else
				{
					parameters.Add("service_display", "none");
				}
			}
			else if (appointmentEmail.AppointmentType == AppointmentType.TestDrive)
			{
				emailTypeKey = "TEST_DRIVE_DEALER";

				if (!string.IsNullOrEmpty(appointmentEmail.Vehicle))
				{
					parameters.Add("service_display", "block");
					parameters.Add("booking_details", appointmentEmail.Vehicle);
				}
				else
				{
					parameters.Add("service_display", "none");
				}
			}

			// add transcript and send
			if (appointmentEmail.IsHTMLEmail)
			{
				var transcript = string.Empty;
				if (!string.IsNullOrEmpty(appointmentEmail.ConversationId) && dealership != null)
				{
					transcript = await _conversationService.GetMessageTranscriptAsync(appointmentEmail.ConversationId, dealership, false, null, null);
				}

				parameters.Add("transcript", transcript);
				await _emailService.SendEmailAsync(emailTypeKey, appointmentEmail.DealerId, appointmentEmail.HTMLEmailToEmails, null, _bccEmail, null, parameters, null).ExecuteWithRetryAsync();
			}

			if (appointmentEmail.IsADFEmail)
			{
				var transcript = string.Empty;
				if (!string.IsNullOrEmpty(appointmentEmail.ConversationId) && dealership != null)
				{
					transcript = await _conversationService.GetMessageTranscriptAsync(appointmentEmail.ConversationId, dealership, true, null, null);
				}

				var adf = GenerateADF(appointmentEmail, transcript, dealership.Name);
				await _emailService.SendAdfEmailAsync(emailTypeKey, appointmentEmail.DealerId, appointmentEmail.ADFEmailToEmails, null, _bccEmail, adf, parameters, null).ExecuteWithRetryAsync();
			}

			// add lead
			if (appointmentEmail.AppointmentType == AppointmentType.LeadGen)
			{
				_analyticsService.ReportLead(
					appointmentEmail.DealerId,
					appointmentEmail.BotUserId,
					appointmentEmail.Name,
					string.IsNullOrEmpty(appointmentEmail.PhoneNum) ? string.Empty : appointmentEmail.PhoneNum,
					string.IsNullOrEmpty(appointmentEmail.Email) ? string.Empty : appointmentEmail.Email,
					0,
					appointmentEmail.ConversationId,
					appointmentEmail.AppointmentType,
					DateTime.UtcNow);
			}
			else
			{
				var utcNow = DateTime.UtcNow;
				var now = System.TimeZoneInfo.ConvertTime(utcNow, TZConvert.GetTimeZoneInfo(Bot.Core.Resource.ResourceConstants.DefaultTimeZone));
				_analyticsService.ReportAppointment(appointmentEmail.DealerId, appointmentEmail.ConversationId, appointmentEmail.AppointmentType, utcNow);

				_appointmentService.AddAppointment(
						appointmentEmail.DealerId,
						appointmentEmail.BotUserId,
						appointmentEmail.Name,
						string.IsNullOrEmpty(appointmentEmail.PhoneNum) ? string.Empty : appointmentEmail.PhoneNum,
						string.IsNullOrEmpty(appointmentEmail.Email) ? string.Empty : appointmentEmail.Email,
						appointmentEmail.Date.ToString(),
						appointmentEmail.Time,
						appointmentEmail.Date,
						appointmentEmail.AppointmentType,
						appointmentEmail.ConversationId,
						0,
						now,
						utcNow,
						null,
						null,
						null,
						null,
						null,
						null);
			}

			return Ok();
		}

		private async Task SendApplePushNotificationMessageAsync(string dealerId, string title, string body)
		{
			var payload = new
			{
				aps = new
				{
					alert = new
					{
						title,
						body,
					},
				},
			};

			var jsonPayload = JsonConvert.SerializeObject(payload);

			var notification = new AppleNotification(jsonPayload)
			{
				Expiry = DateTime.UtcNow.AddMinutes(5),
			};

			await _notificationHubClient.SendNotificationAsync(notification, dealerId);
		}

		private adf GenerateADF(AppointmentEmail appointmentEmail, string transcript, string dealerName, vehicle vehicle = null)
		{
			var adf = new adf();

			var fullname = appointmentEmail.Name.Split(" ");
			var firstnames = string.Empty;
			var lastnames = string.Empty;

			if (fullname.Count() == 1)
			{
				firstnames = fullname[0];
			}
			else if (fullname.Count() > 1)
			{
				firstnames = string.Join(" ", fullname, 0, fullname.Count() - 1);
				lastnames = fullname[fullname.Count() - 1];
			}

			var fname = new name
			{
				part = namePart.first,
				Value = firstnames,
			};

			var lname = new name
			{
				part = namePart.last,
				Value = lastnames,
			};

			var phonenum = new phone
			{
				Value = appointmentEmail.PhoneNum,
			};

			var emailadd = new email
			{
				Value = appointmentEmail.Email,
			};

			var contactinfo = new contact
			{
				name = new List<name>() { fname, lname }.ToArray(),
				phone = new List<phone>() { phonenum }.ToArray(),
				email = emailadd,
			};

			var customers = new customer
			{
				contact = contactinfo,
				comments = transcript,
			};

			string dateTimeParam = appointmentEmail.Date.ToString("yyyy'-'MM'-'dd");
			string timeParam = string.Empty;

			if (DateTime.TryParse(appointmentEmail.Time, out DateTime t))
			{
				dateTimeParam = appointmentEmail.Date.Date.Add(DateTime.Parse(appointmentEmail.Time).TimeOfDay).ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss");
			}

			var prospect = new prospect
			{
				customer = customers,
				requestdate = appointmentEmail.AppointmentType != AppointmentType.LeadGen ?
								dateTimeParam : null,
				vehicle = vehicle == null ? null : new List<vehicle>() { vehicle }.ToArray(),
				vendor = new vendor
				{
					id = new[]
				{
					new id
					{
						sequence = "1",
						source = "DealerAI",
					},
				},
					vendorname = dealerName,
					contact = new contact
					{
						name = new[]
					{
						new name
						{
							part = namePart.full,
							Value = "DealerAI",
						},
					},
						email = new email
						{
							Value = "contact@dealerai.com",
						},
					},
				},
				provider = new provider
				{
					name = new name
					{
						part = namePart.full,
						Value = "DealerAI",
					},
					url = "https://dealerai.com/",
				},
			};

			adf.prospect = new List<prospect>() { prospect }.ToArray();
			return adf;
		}
	}
}
