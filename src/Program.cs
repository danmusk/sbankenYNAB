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
			var customerId = sbankenSettings.Value.CustomerId;
			var mainAccountId = sbankenSettings.Value.MainAccountId;

			/** Setup constants */
			var discoveryEndpoint = "https://auth.sbanken.no/identityserver";
			var apiBaseAddress = "https://api.sbanken.no";
			var bankBasePath = "/exec.bank";
			var customersBasePath = "/exec.customers";

			/**
			 * Connect to Sbanken
			 *
			 * Here the application connect to the identity server endpoint to retrieve a access token.
			 */

			// First: get the OpenId configuration from Sbanken.
			var discoClient = new DiscoveryClient(discoveryEndpoint);

			var x = discoClient.Policy = new DiscoveryPolicy()
			{
				ValidateIssuerName = false,
			};

			var discoResult = await discoClient.GetAsync();

			if (discoResult.Error != null)
			{
				throw new Exception(discoResult.Error);
			}

			// The application now knows how to talk to the token endpoint.

			// Second: the application authenticates against the token endpoint
			var tokenClient = new TokenClient(discoResult.TokenEndpoint, clientId, secret);

			var tokenResponse = tokenClient.RequestClientCredentialsAsync().Result;

			if (tokenResponse.IsError)
			{
				throw new Exception(tokenResponse.ErrorDescription);
			}

			// The application now has an access token.

			var httpClient = new HttpClient()
			{
				BaseAddress = new Uri(apiBaseAddress),
				DefaultRequestHeaders =
				{
					{ "customerId", customerId }
				}
			};

			// Finally: Set the access token on the connecting client.
			// It will be used with all requests against the API endpoints.
			httpClient.SetBearerToken(tokenResponse.AccessToken);

			// The application retrieves the customer's information.
			var customerResponse = await httpClient.GetAsync($"{customersBasePath}/api/v1/Customers");
			var customerResult = await customerResponse.Content.ReadAsStringAsync();

			Console.WriteLine($"CustomerResult:{customerResult}");

			// The application retrieves the customer's accounts.
			var accountResponse = await httpClient.GetAsync($"{bankBasePath}/api/v1/Accounts");
			var accountResult = await accountResponse.Content.ReadAsStringAsync();
			var accountsList = JsonConvert.DeserializeObject<AccountsList>(accountResult);

			Console.WriteLine($"AccountResult:{accountResult}");

			var spesificAccountResponse = await httpClient.GetAsync($"{bankBasePath}/api/v1/Accounts/{accountsList.Items[0].AccountId}");
			var spesificAccountResult = await spesificAccountResponse.Content.ReadAsStringAsync();

			var accountTransactionsResponse = await httpClient.GetAsync($"{bankBasePath}/api/v1/Transactions/{mainAccountId}?length=250");
			var accountTransactionsResult = await accountTransactionsResponse.Content.ReadAsStringAsync();
			var transactionsList = JsonConvert.DeserializeObject<WrapperList<Transaction>>(accountTransactionsResult);

			Console.WriteLine($"accountTransactionsResult:{accountTransactionsResult}");

			foreach (var transaction in transactionsList.Items)
			{
				Console.WriteLine($"Text:{transaction.Text}\nOriginalText: {transaction.OriginalText}\nAmount: {transaction.Amount}");
			}


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
				col.EnsureIndex(t => t.HashCode);

				// LOOP
                var transactionsNotReadyForTransfer = transactionsList.Items.Where(t => !t.ReadyForTransfer).ToList();
                var transactionsReadyForTransfer = transactionsList.Items.Where(t => t.ReadyForTransfer).ToList();
				foreach (var transaction in transactionsReadyForTransfer)
				{
					var r = col.FindOne(t => t.HashCode.Equals(transaction.HashCode));
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
									payeeName: transaction.Text
                                ));

                        await ynabApi.Transactions.CreateTransactionAsync(budgetId, addTransaction);

                        col.Insert(transaction);

						Console.WriteLine($"Transaction transfered: {transaction.AccountingDate:O}: {transaction.Text} - {transaction.Amount}");
					}
					else
					{
						Console.WriteLine($"Transaction already transfered: {transaction.AccountingDate:O}: {transaction.Text} - {transaction.Amount}");
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
		public int Id { get; set; }
		public DateTime AccountingDate { get; set; }
		public double Amount { get; set; }
		public bool IsReservation { get; set; }

        public bool ReadyForTransfer
        {
            get
            {
                if (IsReservation)
                {
                    return false;
                }
                if (Text == "Nettbank"
                    || Text == "Overførsel"
                    || Text == "Nettgiro"
                    || Text == "Straksoverføring"
                    || Text == "Overført Til Annen Konto"
                    || Text == "Efaktura Avtalegiro")
                {
                    return false;
                }
                return true;
            }
        }

        public string OriginalText => _text;
		public string Text
		{
			get
			{
				var result = _text;

				result = result
					.Replace("*7424", "")
					.Replace("*4137", "")
					.Replace("NOK", "")
					.Replace("SEK", "")
					.Replace("Betalt:", "")
					.Replace(Math.Abs(Amount).ToString("F").Replace(",", "."), "")
					.Replace("Til:", "")
					.Replace("Fra:", "")
					.Replace("Kurs:", "")
					.Replace("1.0000", "");

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

		public string HashCode => GetStringSha256Hash($"{this.AccountingDate:O}-{this.OriginalText}-{this.Amount}");

		public static string GetStringSha256Hash(string text)
		{
			if (String.IsNullOrEmpty(text))
				return String.Empty;

			using (var sha = new System.Security.Cryptography.SHA256Managed())
			{
				byte[] textData = System.Text.Encoding.UTF8.GetBytes(text);
				byte[] hash = sha.ComputeHash(textData);
				return BitConverter.ToString(hash).Replace("-", String.Empty);
			}
		}
	}
}


