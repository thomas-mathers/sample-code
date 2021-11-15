// <copyright file="WebChatController.cs" company="Idea Notion Development Inc">
// Copyright (c) Idea Notion Development Inc. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Bot.Core.Model.Document;
using Bot.Core.Model.Entity;
using Bot.Core.Service;
using Bot.Web.Attributes;
using Bot.Web.DTOs;
using Bot.Web.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Bot.Web.Controllers
{
	[ApiExceptionFilter]
	[Produces("application/json")]
	[Route(template: "api/webchat")]
	public class WebChatController : Controller
	{
		private readonly WebChatConfigurationService webChatConfigurationService;
		private readonly DealershipService dealershipService;
		private readonly GeneralBotService generalBotService;
		private readonly string webChatBaseUrl;
		private readonly string portalBaseUrl;
		private readonly BotDbContext _context;
		private readonly BotDbContext database;

		public WebChatController(IConfiguration configuration, WebChatConfigurationService webChatConfigurationService, DealershipService dealershipService, GeneralBotService generalBotService, BotDbContext database, BotDbContext context)
		{
			this.webChatConfigurationService = webChatConfigurationService;
			this.dealershipService = dealershipService;
			this.generalBotService = generalBotService;

			this.webChatBaseUrl = configuration["WebChatBaseUrl"];
			this.portalBaseUrl = configuration["PortalBaseUrl"];

			this.database = database;
			this._context = context;
		}

		[HttpPost]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		public async Task<ActionResult> UpsertAsync(string dealerId, [FromBody] WebChatConfiguration body)
		{
			foreach (var m in body.ActiveMessages)
			{
				m.Url = m.Url.Replace("\\\\", "\\"); // in ui, \\ will become \
			}

			await webChatConfigurationService.UpsertAsync(body);
			return Ok();
		}

		[HttpGet]
		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[ClaimsFilter]
		public async Task<ActionResult<WebChatConfigurationEx>> Get(string dealerId)
		{
			var webChatConfiguration = await GetWebChatConfigurationById(dealerId);

			return Ok(new WebChatConfigurationEx(webChatConfiguration) { WebChatBaseUrl = webChatBaseUrl, PortalBaseUrl = portalBaseUrl });
		}

		[HttpPost]
		[Route("legacyscript")]
		public async Task<string> Generate([FromBody] GenerateWebChatScript json)
		{
			var dealer = await database.Dealers.Include(t => t.FacebookPage)
				.Include(t => t.Manufacturer)
				.FirstAsync(x => x.Id == json.DealerId);

			var sb = new StringBuilder();

			if (json.UseFacebook)
			{
				if (json.AlignLeft)
				{
					sb.AppendLine("<style>");
					sb.AppendLine(".fb_dialog {left: 52pt;}");
					sb.AppendLine(".fb_iframe_widget iframe {left: 36pt;}");
					sb.AppendLine(".fb_customer_chat_bounce_out_v2 {animation - name: fb_bounce_out_custom !important;animation - fill - mode: forwards;}");
					sb.AppendLine(".fb_customer_chat_bounce_in_v2 {animation - name: fb_bounce_in_custom !important;animation - fill - mode: forwards;}");
					sb.AppendLine("@keyframes fb_bounce_in_custom{");
					sb.AppendLine("0 % {opacity: 0;transform: scale(0, 0);transform - origin: bottom left;}");
					sb.AppendLine("50 % {transform: scale(1.03, 1.03);transform - origin: bottom left;}");
					sb.AppendLine("100 % {opacity: 1;transform: scale(1, 1);transform - origin: bottom left;}}");
					sb.AppendLine("@keyframes fb_bounce_out_custom{");
					sb.AppendLine("0 % {opacity: 1;transform: scale(1, 1);transform - origin: bottom left;}");
					sb.AppendLine("100 % {opacity: 0;transform: scale(0, 0);transform - origin: bottom left;max - height: 0 % !important;}}");
					sb.AppendLine("</style>");
				}

				sb.AppendLine("<div id='fb-root'></div>");
				sb.AppendLine("<script>");
				sb.AppendLine("(function(d, s, id) {");
				sb.AppendLine("var js, fjs = d.getElementsByTagName(s)[0]");
				sb.AppendLine("if (d.getElementById(id)) return;");
				sb.AppendLine("js = d.createElement(s); js.id = id;");
				sb.AppendLine("js.src = 'https://connect.facebook.net/en_US/sdk/xfbml.customerchat.js#xfbml=1&version=v2.12&autoLogAppEvents=1';");
				sb.AppendLine("fjs.parentNode.insertBefore(js, fjs);");
				sb.AppendLine("}(document, 'script', 'facebook-jssdk'));");
				sb.AppendLine("</script>");
				sb.AppendLine($"<div class='fb-customerchat' page_id='{dealer.FacebookPage.PageId}'></div>");
			}

			if (json.UseWebchat)
			{
				var show = !json.AutoShow ? "data-dont-auto-show='True'" : string.Empty;
				sb.AppendLine($"<div id='bot' {show} data-align='{(json.AlignLeft ? "left" : "right")}' data-use-header='true' data-dealer-id='{json.DealerId}' data-dealer-name='{dealer.DealerName}' data-direct-line-secret='{dealer.DirectChatKey}' data-accent='#{dealer.Manufacturer.ColourCode}' data-header-logo='https://dealeraibot.azureedge.net/logos{dealer.Manufacturer.LogoUrl}' data-header-text='Welcome to {dealer.DealerName}! Chat with our friendly Artificial Intelligent Bot below.'><div id='bot-messages'></div><div id='bot-button' onClick='openChatMessages()'></div></div>");
				sb.AppendLine($"<script src='https://dealeraibot.azureedge.net/webchat/latest/wc.min.js'></script>");
			}

			return sb.ToString();
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		[Route("dialog-and-locale/admin")]
		[HttpGet]
		public dynamic GetDialogAndLocaleListByAdmin()
		{
			var dialogList = generalBotService.GetDialogListWithoutCache("DIALOG_INFO");
			var localeList = generalBotService.GetLocaleListWithoutCache("BOT_SUPPORTED_LOCALES");
			return Ok(new { dialogList, localeList });
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme, Roles = "SiteAdmin")]
		[Route("dialog-info/admin")]
		[HttpGet]
		public dynamic GetDialogListByAdmin()
		{
			var dialogList = generalBotService.GetDialogListWithoutCache("DIALOG_INFO");
			return Ok(dialogList);
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[Route("disable-messages")]
		[ClaimsFilter]
		[HttpGet]
		public async Task<ActionResult<DisableMessages>> GetDisableMessagesWithLocale(string dealerId)
		{
			// define string text keys
			var salesKey = "DISABLED_SALES_APPOINTMENT";
			var serviceKey = "DISABLED_SERVICE_APPOINTMENT";
			var testDriveKey = "DISABLED_TESTDRIVE_APPOINTMENT";

			// find all supported locales for dealer
			var webChatConfiguration = await GetWebChatConfigurationById(dealerId);
			var supportedLocales = webChatConfiguration.SupportedLocales;

			// get all disable messages for dealer
			var allMessages = _context.StringTexts
				.Where(t => t.DealerId == dealerId && (t.Key == salesKey || t.Key == serviceKey || t.Key == testDriveKey))
				.ToList();

			// find all valid messages (based on locale)
			var salesValid = new List<SingleDisableMessage>();
			var serviceValid = new List<SingleDisableMessage>();
			var testDriveValid = new List<SingleDisableMessage>();

			foreach (var lang in supportedLocales)
			{
				var salesMsg = GetValidStringText(salesKey, lang, allMessages, dealerId);
				salesValid = salesValid.Concat(salesMsg).ToList();

				var serviceMsg = GetValidStringText(serviceKey, lang, allMessages, dealerId);
				serviceValid = serviceValid.Concat(serviceMsg).ToList();

				var testDriveMsg = GetValidStringText(testDriveKey, lang, allMessages, dealerId);
				testDriveValid = testDriveValid.Concat(testDriveMsg).ToList();
			}

			var result = new DisableMessages
			{
				SalesDisableMessages = salesValid.Distinct(new SingleDisableMessageComparer()).ToList(),
				ServiceDisableMessages = serviceValid.Distinct(new SingleDisableMessageComparer()).ToList(),
				TestDriveDisableMessages = testDriveValid.Distinct(new SingleDisableMessageComparer()).ToList(),
				SaveSales = false,
				SaveService = false,
				SaveTestDrive = false,
			};

			return Ok(result);
		}

		[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
		[Route("update-disable-messages")]
		[ClaimsFilter]
		[HttpPost]
		public async Task<ActionResult<DisableMessages>> UpdateDisableMessages(string dealerId, [FromBody] DisableMessages disableMessages)
		{
			// define string text keys
			var salesKey = "DISABLED_SALES_APPOINTMENT";
			var serviceKey = "DISABLED_SERVICE_APPOINTMENT";
			var testDriveKey = "DISABLED_TESTDRIVE_APPOINTMENT";

			// get all disable messages for dealer
			var allMessages = _context.StringTexts
				.Where(t => t.DealerId == dealerId && (t.Key == salesKey || t.Key == serviceKey || t.Key == testDriveKey))
				.ToList();

			// define messages lists
			var salesValid = new List<SingleDisableMessage>();
			var serviceValid = new List<SingleDisableMessage>();
			var testDriveValid = new List<SingleDisableMessage>();

			// update disable messages
			// sales
			if (disableMessages.SaveSales)
			{
				foreach (var salesMsg in disableMessages.SalesDisableMessages)
				{
					if (string.IsNullOrEmpty(salesMsg.Id))
					{
						// insert
						var newSalesMsg = new StringText
						{
							Id = Guid.NewGuid(),
							Key = salesKey,
							DealerId = dealerId,
							Locale = salesMsg.Locale,
							Text = salesMsg.Text,
							Weight = 1,
							CreatedDate = DateTime.UtcNow,
							LastModifiedDate = DateTime.UtcNow,
							ManufacturerId = null,
						};
						_context.StringTexts.Add(newSalesMsg);

						// prepare return result
						salesValid.Add(new SingleDisableMessage
						{
							Id = newSalesMsg.Id.ToString(),
							Key = salesKey,
							DealerId = dealerId,
							Locale = newSalesMsg.Locale,
							LocaleName = salesMsg.LocaleName,
							Text = newSalesMsg.Text,
						});
					}
					else
					{
						// update
						var salesMsgFound = allMessages.FirstOrDefault(m => m.Id == new Guid(salesMsg.Id));
						salesMsgFound.Text = salesMsg.Text;
						salesMsgFound.LastModifiedDate = DateTime.UtcNow;
						_context.StringTexts.Update(salesMsgFound);

						// prepare return result
						salesValid.Add(new SingleDisableMessage
						{
							Id = salesMsgFound.Id.ToString(),
							Key = salesKey,
							DealerId = dealerId,
							Locale = salesMsgFound.Locale,
							LocaleName = salesMsg.LocaleName,
							Text = salesMsgFound.Text,
						});
					}
				}
			}
			else
			{
				salesValid = disableMessages.SalesDisableMessages;
			}

			// service
			if (disableMessages.SaveService)
			{
				foreach (var serviceMsg in disableMessages.ServiceDisableMessages)
				{
					if (string.IsNullOrEmpty(serviceMsg.Id))
					{
						// insert
						var newServiceMsg = new StringText
						{
							Id = Guid.NewGuid(),
							Key = serviceKey,
							DealerId = dealerId,
							Locale = serviceMsg.Locale,
							Text = serviceMsg.Text,
							Weight = 1,
							CreatedDate = DateTime.UtcNow,
							LastModifiedDate = DateTime.UtcNow,
							ManufacturerId = null,
						};
						_context.StringTexts.Add(newServiceMsg);

						// prepare return result
						serviceValid.Add(new SingleDisableMessage
						{
							Id = newServiceMsg.Id.ToString(),
							Key = serviceKey,
							DealerId = dealerId,
							Locale = newServiceMsg.Locale,
							LocaleName = serviceMsg.LocaleName,
							Text = newServiceMsg.Text,
						});
					}
					else
					{
						// update
						var serviceMsgFound = allMessages.FirstOrDefault(m => m.Id == new Guid(serviceMsg.Id));
						serviceMsgFound.Text = serviceMsg.Text;
						serviceMsgFound.LastModifiedDate = DateTime.UtcNow;
						_context.StringTexts.Update(serviceMsgFound);

						// prepare return result
						serviceValid.Add(new SingleDisableMessage
						{
							Id = serviceMsgFound.Id.ToString(),
							Key = serviceKey,
							DealerId = dealerId,
							Locale = serviceMsgFound.Locale,
							LocaleName = serviceMsg.LocaleName,
							Text = serviceMsgFound.Text,
						});
					}
				}
			}
			else
			{
				serviceValid = disableMessages.ServiceDisableMessages;
			}

			// test drive
			if (disableMessages.SaveTestDrive)
			{
				foreach (var testDriveMsg in disableMessages.TestDriveDisableMessages)
				{
					if (string.IsNullOrEmpty(testDriveMsg.Id))
					{
						// insert
						var newTestDriveMsg = new StringText
						{
							Id = Guid.NewGuid(),
							Key = testDriveKey,
							DealerId = dealerId,
							Locale = testDriveMsg.Locale,
							Text = testDriveMsg.Text,
							Weight = 1,
							CreatedDate = DateTime.UtcNow,
							LastModifiedDate = DateTime.UtcNow,
							ManufacturerId = null,
						};
						_context.StringTexts.Add(newTestDriveMsg);

						// prepare return result
						testDriveValid.Add(new SingleDisableMessage
						{
							Id = newTestDriveMsg.Id.ToString(),
							Key = testDriveKey,
							DealerId = dealerId,
							Locale = newTestDriveMsg.Locale,
							LocaleName = testDriveMsg.LocaleName,
							Text = newTestDriveMsg.Text,
						});
					}
					else
					{
						// update
						var testDriveMsgFound = allMessages.FirstOrDefault(m => m.Id == new Guid(testDriveMsg.Id));
						testDriveMsgFound.Text = testDriveMsg.Text;
						testDriveMsgFound.LastModifiedDate = DateTime.UtcNow;
						_context.StringTexts.Update(testDriveMsgFound);

						// prepare return result
						testDriveValid.Add(new SingleDisableMessage
						{
							Id = testDriveMsgFound.Id.ToString(),
							Key = testDriveKey,
							DealerId = dealerId,
							Locale = testDriveMsgFound.Locale,
							LocaleName = testDriveMsg.LocaleName,
							Text = testDriveMsgFound.Text,
						});
					}
				}
			}
			else
			{
				testDriveValid = disableMessages.TestDriveDisableMessages;
			}

			await _context.SaveChangesAsync();

			// define return object
			var result = new DisableMessages
			{
				SalesDisableMessages = salesValid,
				ServiceDisableMessages = serviceValid,
				TestDriveDisableMessages = testDriveValid,
				SaveSales = false,
				SaveService = false,
				SaveTestDrive = false,
			};

			return Ok(result);
		}

		// helper functions
		private List<SingleDisableMessage> GetValidStringText(string stringKey, Locale locale, List<StringText> allMessages, string dealerId)
		{
			try
			{
				var partialCode = locale.Code.Split("-")[0];

				var validMsg = allMessages
					.Where(m => (m.Locale == locale.Code) && m.Key == stringKey);

				if (validMsg == null || validMsg.Count() == 0)
				{
					validMsg = allMessages
						.Where(m => m.Locale == partialCode && m.Key == stringKey);
				}

				var result = new List<SingleDisableMessage>();

				// no msg is found when try with local code and partial code
				if (validMsg == null || validMsg.Count() == 0)
				{
					var defaultMsg = new SingleDisableMessage
					{
						Id = string.Empty,
						Key = stringKey,
						DealerId = dealerId,
						Locale = partialCode,
						LocaleName = locale.Name,
						Text = string.Empty,
					};
					result.Add(defaultMsg);
				}
				else
				{
					// msg is found
					result = validMsg
					.Select(m => new SingleDisableMessage
					{
						Id = m.Id.ToString(),
						Key = m.Key,
						DealerId = m.DealerId,
						Locale = m.Locale,
						LocaleName = locale.Name,
						Text = m.Text,
					})
					.ToList();
				}

				// return list of a single message
				return result;
			}
			catch (Exception e)
			{
				throw new Exception(e.Message);
			}

		}

		private async Task<WebChatConfiguration> GetWebChatConfigurationById(string dealerId)
		{
			var webChatConfiguration = webChatConfigurationService.Get(dealerId);

			if (webChatConfiguration == null)
			{
				var dealership = dealershipService.GetDealer(dealerId);
				var dealer = await database.Dealers
					.Include(t => t.Manufacturer)
					.FirstAsync(x => x.Id == dealerId);

				var defaultLocale = new Locale
				{
					Display = "en",
					Code = "en-US",
					Name = "English",
					BotFrameworkLocale = "en-US",
				};

				webChatConfiguration = new WebChatConfiguration
				{
					Id = Guid.NewGuid().ToString(),
					DealerId = dealerId,
					DealerName = dealership.Name,
					AccentColor = "#" + dealer.Manufacturer.ColourCode,
					LogoUrl = $"https://dealeraibot.azureedge.net/logos{dealer.Manufacturer.LogoUrl}",
					WelcomeMessagePrimary = $"Welcome to {dealership.Name}",
					WelcomeMessageSecondary = "Hi, I am your AI assistant. Let's chat!",
					AutoPopup = false,
					AutoPopupDelayMs = 3000,
					AlignRight = true,
					AlwaysAutoPopup = false,
					PopupStyle = 2,
					AutoPopupMode = 1,
					AuthWindowStyles = new int[] { 2 },
					SupportedLocales = new Locale[] { defaultLocale },
				};

				await webChatConfigurationService.UpsertAsync(webChatConfiguration);
			}
			else
			{
				foreach (var m in webChatConfiguration.ActiveMessages)
				{
					m.Url = m.Url.Replace("\\", "\\\\"); // in ui, \\ will become \
				}
			}

			return webChatConfiguration;
		}
	}
}
