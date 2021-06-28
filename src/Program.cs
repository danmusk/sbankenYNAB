using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IdentityModel.Client;
using LiteDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using YNAB.SDK.Model;

namespace SbankenYNAB
{
	class Program
	{
        public static IConfigurationRoot Configuration { get; set; }

        static void Main(string[] args)
		{
            var builder = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddEnvironmentVariables();
            builder.AddUserSecrets<YNABSettings>();
            builder.AddUserSecrets<SbankenSettings>();
            Configuration = builder.Build();

            var services = new ServiceCollection()
                .Configure<YNABSettings>(Configuration.GetSection(nameof(YNABSettings)))
                .Configure<SbankenSettings>(Configuration.GetSection(nameof(SbankenSettings)))
                .AddOptions()
                .BuildServiceProvider();

            RunAsync(services).Wait();
		}

		static async Task RunAsync(ServiceProvider services)
		{
            /*
            Client credentials and customerId

            Here Oauth2 is being used with "client credentials": The "client" is the application, and we require a secret
            known only to the application.

             */

            var sbankenSettings = services.GetService<IOptions<SbankenSettings>>();
            var clientId = sbankenSettings.Value.ClientId;
			var secret = sbankenSettings.Value.Secret;
			var regningsKontoAccountId = sbankenSettings.Value.MainAccountId;

			/** Setup constants */
			var discoveryEndpoint = "https://auth.sbanken.no/identityserver";
			var apiBaseAddress = "https://publicapi.sbanken.no/apibeta";

			/**
			 * Connect to Sbanken
			 *
			 * Here the application connect to the identity server endpoint to retrieve a access token.
			 */

			// First: get the OpenId configuration from Sbanken.
			var discoHttpClient = new HttpClient();
            var discoveryDocumentResponse = await discoHttpClient.GetDiscoveryDocumentAsync(discoveryEndpoint);

            if (discoveryDocumentResponse.Error != null)
            {
                throw new Exception(discoveryDocumentResponse.Error);
            }

            var tokenClient = new HttpClient();

			// Second: the application authenticates against the token endpoint
            var tokenRequest = new ClientCredentialsTokenRequest()
            {
                Address = discoveryDocumentResponse.TokenEndpoint,
                ClientId = clientId,
                ClientSecret = secret
            };

            var tokenResponse = await tokenClient.RequestClientCredentialsTokenAsync(tokenRequest);

			if (tokenResponse.IsError)
			{
				throw new Exception(tokenResponse.Error);
			}

			// The application now has an access token.

			var sBankenHttpClient = new HttpClient()
			{
				BaseAddress = new Uri(apiBaseAddress)
			};

			// Finally: Set the access token on the connecting client.
			// It will be used with all requests against the API endpoints.
			sBankenHttpClient.SetBearerToken(tokenResponse.AccessToken);

			var accountTransactionsResponse = await sBankenHttpClient.GetAsync($"/apibeta/api/v2/Transactions/archive/{regningsKontoAccountId}?length=200");
			var accountTransactionsResult = await accountTransactionsResponse.Content.ReadAsStringAsync();
			var transactionsListWrapper = JsonConvert.DeserializeObject<WrapperList<Transaction>>(accountTransactionsResult);
			var transactionsList = transactionsListWrapper.Items.OrderByDescending(x => x.AccountingDate).ToList();

			//Console.WriteLine($"accountTransactionsResult:{accountTransactionsResult}");

//			foreach (var transaction in transactionsList)
//			{
//				Console.WriteLine($@"
//TransactionId: {transaction.TransactionId}
//Date: {transaction.AccountingDate:dd.MM.yyyy}
//Text: {transaction.Text}
//OriginalText: {transaction.OriginalText}
//Amount: {transaction.Amount}");
//			}

            // YNAB
            var ynabSettings = services.GetService<IOptions<YNABSettings>>();
            var accessToken = ynabSettings.Value.AccessToken;
			var ynabApi = new YNAB.SDK.API(accessToken);
			var budgetsResponse = await ynabApi.Budgets.GetBudgetsAsync();
			budgetsResponse.Data.Budgets.ForEach(budget =>
			{
				Console.WriteLine($"Budget Name: {budget.Name}");
			});

			var budgetId = budgetsResponse.Data.Budgets.First(b => b.Name.Contains("FOV27")).Id.ToString();
			var accountsResponse = await ynabApi.Accounts.GetAccountsAsync(budgetId);
			accountsResponse.Data.Accounts.ForEach(account =>
			{
				Console.WriteLine($"Account Name: {account.Name}");
			});

			var accountId = accountsResponse.Data.Accounts.First(a => a.Name.Contains("Regningskonto")).Id;

			using (var db = new LiteDatabase(@"C:\ProgramData\SbankenYNAB\SbankenYNAB.db"))
			{
				// Get a collection (or create, if doesn't exist)
				var col = db.GetCollection<Transaction>("transactions");
				col.EnsureIndex(t => t.TransactionId);

				// LOOP
				foreach (var transaction in transactionsList)
				{
					var r = col.FindOne(t => t.TransactionId.Equals(transaction.TransactionId));
					if (r == null)
					{
                        var milliUnitLong = long.Parse(transaction.Amount.ToMilliUnit());
						var addTransaction =
							new SaveTransactionsWrapper(
								new SaveTransaction(
									accountId,
									transaction.AccountingDate,
									milliUnitLong,
									cleared: SaveTransaction.ClearedEnum.Uncleared,
									flagColor: milliUnitLong >= 0
										? SaveTransaction.FlagColorEnum.Green
										: (SaveTransaction.FlagColorEnum?) null,
									payeeName: transaction.Text,
                                    importId: transaction.TransactionId
                                ));

                        await ynabApi.Transactions.CreateTransactionAsync(budgetId, addTransaction);

                        col.Insert(transaction);

						Console.WriteLine($"Transaction transferred: {transaction.AccountingDate:O}: {transaction.Text} - {transaction.Amount}");
					}
					else
					{
						Console.WriteLine($"Transaction already transferred: {transaction.AccountingDate:O}: {transaction.Text} - {transaction.Amount}");
					}
				}
			}
			Console.WriteLine("Done...");
		}
	}

	public static class ExtensionMethod
	{
		public static string ToMilliUnit(this double value)
		{
			return value.ToString("##.##0").Replace(",", "").Replace(".", "");
		}

		public static string ToTitleCase(this string title)
		{
			return CultureInfo.CurrentCulture.TextInfo.ToTitleCase(title.ToLower());
		}
	}

	public class WrapperList<T>
	{
		public List<T> Items { get; set; }
	}

	public class Transaction
	{
		private string _text;
		public string TransactionId { get; set; }
		public DateTime AccountingDate { get; set; }
		public double Amount { get; set; }

        public string OriginalText => _text;
		public string Text
		{
			get
			{
				var result = _text;

				result = result.ToLower()
					.Replace("*7424", "")
					.Replace("*4137", "")
					.Replace("nok", "")
					.Replace(".no", "")
					.Replace("SEK", "")
					.Replace("Betalt", "")
					.Replace(Math.Abs(Amount).ToString("F").Replace(",", "."), "")
					.Replace("Til", "")
					.Replace("Fra", "")
					.Replace("Kurs", "")
					.Replace("1.0000", "")
					.Replace(":", "");

				// dd.mm.yy
				var regex = new Regex(@"(([0-9]{2}).([0-9]{2}).([0-9]{2}))");
				var match = regex.Match(result);
				if (match.Success)
				{
					result = result.Replace(match.Value, "");
				}

				// dd.mm
				regex = new Regex(@"((0[1-9]|[12]\d|3[01]).(0[1-9]|1[0-2]))");
				match = regex.Match(result);
				if (match.Success)
				{
					result = result.Replace(match.Value, "");
				}

				// doble spaces
				RegexOptions options = RegexOptions.None;
				regex = new Regex("[ ]{2,}", options);
				result = regex.Replace(result, " ");

				// casing
				result = result.ToTitleCase();

				return result.Trim();
			}
			set => _text = value;
		}
    }
}
