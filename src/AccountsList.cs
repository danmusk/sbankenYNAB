using System.Collections.Generic;

namespace SbankenYNAB
{
    public class AccountsList
    {
        public int AvailableItems { get; set; }
        public List<Account> Items { get; set; }
    }

    public class Account
    {
        public string AccountId { get; set; }
        // add additional fields as needed to fully describe the account structure. See: https://api.sbanken.no/exec.bank/swagger

    }
}
