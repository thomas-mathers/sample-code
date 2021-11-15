using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using FBC.Web.DTO.FacebookApiObjects;
using FBC.Web.DTO.FacebookApiObjects.Enums;
using FBC.Web.Services;

namespace FBC.Web.Controllers
{
    [ApiExplorerSettings(IgnoreApi = true)]
    [ApiController]
    [Route("api/[controller]")]
    public class FacebookRFPController : ControllerBase
    {
        private readonly ILogger<FacebookRFPController> logger;
        private readonly IConfiguration configuration;
        private readonly FacebookWorkplaceService workplaceService;
        private readonly TranslationService translationService;
        private readonly string RFP_BOT_SOURCE = "rfp";

        public FacebookRFPController(ILogger<FacebookRFPController> logger, IConfiguration configuration, FacebookWorkplaceService workplaceService, TranslationService translationService)
        {
            this.logger = logger;
            this.configuration = configuration;
            this.workplaceService = workplaceService;
            this.translationService = translationService;
        }

        [HttpPost()]
        [Produces("text/plain")]
        public async Task<ActionResult> Post()
        {
            logger.LogTrace("Enter Post");

            var bodyText = string.Empty;
            var body = new Event();

            using (var sr = new StreamReader(Request.Body))
            {
                bodyText = await sr.ReadToEndAsync();

                try
                {
                    body = JsonConvert.DeserializeObject<Event>(bodyText);
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }

            logger.LogDebug("bodyText = {0}", bodyText);

            /// validate Facebook message signature
            if (Request.Headers.ContainsKey("X-Hub-Signature") == false)
            {
                logger.LogWarning("X-Hub-Signature missing");
                return Unauthorized();
            }

            var expectedSignature = Request.Headers["X-Hub-Signature"].ToString().Replace("sha1=", string.Empty);
            var signature = HashHmac("HMACSHA1", EncodeNonAsciiCharacters(bodyText), configuration["FacebookRFPBot:AppSecret"]);
            if (signature != expectedSignature)
            {
                logger.LogWarning("Facebook event signature does not match expected signature. Got = {0}. Expected = {1}", signature, expectedSignature);
                return Unauthorized();
            }

            // only parse if it is a page event
            if (body.Object != "page")
            {
                logger.LogWarning("Facebook event type {0} not supported", body.Object);
                return NotFound();
            }


            foreach (var e in body.Entries)
            {
                foreach (var messaging in e.Messaging)
                {
                    try
                    {
                        if (messaging.Postback != null)
                        {
                            // handle post back button
                            await HandlePostbackAsync(messaging.Sender, messaging.Postback);
                        }
                        else if (messaging.Message != null)
                        {
                            // handle user enter message
                            await HandleMessageAsync(messaging.Sender, messaging.Message);
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, string.Empty);
                    }
                }
            }

            logger.LogTrace("Exit Post");

            return Ok("EVENT_RECEIVED");
        }

        [HttpGet()]
        [Produces("text/plain")]
        public ActionResult Get([FromQuery(Name = "hub.mode")] string mode = null,
                                [FromQuery(Name = "hub.verify_token")] string token = null,
                                [FromQuery(Name = "hub.challenge")] string challenge = null)
        {
            logger.LogTrace("Enter Get");
            logger.LogDebug("mode = {0}, token = {1}, challenge = {2}", mode, token, challenge);

            if (mode == null || token == null)
            {
                logger.LogTrace("Exit Get");
                return Ok();
            }
            else if (mode == "subscribe" && token == configuration["FacebookRFPBot:VerifyToken"])
            {
                logger.LogInformation("Subscription successful");
                logger.LogTrace("Exit Get");
                return Ok(challenge);
            }
            else
            {
                logger.LogTrace("Exit Get");
                return Unauthorized();
            }
        }

        [NonAction]
        private async Task HandleMessageAsync(User user, Message message)
        {
            logger.LogTrace("Enter RFP HandleMessageAsync");
            logger.LogDebug("user = {0}, message = {1}", user, message);

            if (user == null)
                throw new ArgumentNullException("user");
            if (user.Id == null)
                throw new ArgumentNullException("user.Id");
            if (user.Community == null)
                throw new ArgumentNullException("user.Community");
            if (user.Community.Id == null)
                throw new ArgumentNullException("user.Community.Id");
            if (message == null)
                throw new ArgumentNullException("message");

            var userObject = await workplaceService.GetFacebookUserById(user.Id);

            // send text message
            if (message.Text.ToLower() == configuration["FacebookRFPBot:GetStarted"].ToLower())
            {
                await SendTextMessage(user.Id, translationService.GetString("GetStartedMessageRFP", userObject.Locale));
            }
            else
            {
                await SendTextMessage(user.Id, translationService.GetString("ChatNotSupportedMessageRFP", userObject.Locale));
            }

            // send options buttons
            await SendAppOptionsAsync(user.Id, userObject.Locale);

            logger.LogTrace("Exit HandleMessageAsync");
        }

        [NonAction]
        private async Task HandlePostbackAsync(User user, Postback postback)
        {
            logger.LogTrace("Enter RFP HandleMessageAsync");
            logger.LogDebug("user = {0}, postback payload = {1}", user, postback.Payload);

            if (user == null)
                throw new ArgumentNullException("user");
            if (user.Id == null)
                throw new ArgumentNullException("user.Id");
            if (user.Community == null)
                throw new ArgumentNullException("user.Community");
            if (user.Community.Id == null)
                throw new ArgumentNullException("user.Community.Id");
            if (postback == null)
                throw new ArgumentNullException("message");

            var userObject = await workplaceService.GetFacebookUserById(user.Id);

            // send text message & options buttons
            if (postback.Payload.ToLower() == configuration["FacebookRFPBot:GetStarted"].ToLower())
            {
                await SendTextMessage(user.Id, translationService.GetString("GetStartedMessageRFP", userObject.Locale));
                await SendAppOptionsAsync(user.Id, userObject.Locale);
            }

            logger.LogTrace("Exit HandleMessageAsync");
        }

        [NonAction]
        private async Task<SendMessageResponse> SendTextMessage(string userId, string text)
        {
            return await workplaceService.SendMessageAsync(userId, new Message { Text = text }, RFP_BOT_SOURCE);
        }

        [NonAction]
        private async Task<SendMessageResponse> SendAppOptionsAsync(string userId, string locale)
        {
            var host = configuration["HostUrl"];
            var message = new Message
            {
                Attachment = new Attachment
                {
                    Type = AttachmentType.Template,
                    Payload = new ButtonsPayload
                    {
                        Text = translationService.GetString("ChooseAnAction", locale),
                        Buttons = new[]
                        {
                            new Button
                            {
                                Title = translationService.GetString("RFP", locale),
                                Type = ButtonType.WebURL,
                                Url = $"{host}/login?from=workplace",
                                WebviewHeightRatio = WebViewHeightRatio.Full,
                                MessengerExtensions = true
                            }
                        }
                    },
                }
            };

            return await workplaceService.SendMessageAsync(userId, message, RFP_BOT_SOURCE);
        }

        [NonAction]
        private string HashHmac(string hashAlgorithm, string data, string key)
        {
            var hmac = HMAC.Create(hashAlgorithm);
            hmac.Key = Encoding.UTF8.GetBytes(key);
            var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
            return ByteArrayToString(hash).ToLower();
        }

        [NonAction]
        private string EncodeNonAsciiCharacters(string value)
        {
            var sb = new StringBuilder();
            foreach (var c in value)
            {
                if (c > 127)
                {
                    var encodedValue = "\\u" + ((int)c).ToString("x4");
                    sb.Append(encodedValue);
                }
                else
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        [NonAction]
        private string ByteArrayToString(byte[] ba)
        {
            return BitConverter.ToString(ba).Replace("-", "");
        }
    }
}
