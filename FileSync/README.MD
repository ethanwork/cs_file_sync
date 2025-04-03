Create a config.json file and place it in the same directoy as the executable. An example config.json is below, you'll need to update the localPath's to be whereever they would be on your system for the matching cloud folder, and the credentials is where you'll paste the dropbox token value


## Step 1: Generate a Refresh Token
You can’t get a refresh token directly from the “Generate” button in the App Console—that only gives short-lived tokens for testing. Instead, you need to go through the OAuth 2 flow with “offline” access:
Go to the Authorization URL:
In your browser, visit:

https://www.dropbox.com/oauth2/authorize?client_id=<YOUR_APP_KEY>&response_type=code&token_access_type=offline

Replace <YOUR_APP_KEY> with your app’s key from the App Console (found under the “Settings” tab).

The token_access_type=offline parameter is crucial—it ensures you get a refresh token.

## Authorize Your App:
Log in to your Dropbox account and authorize the app. You’ll see a consent screen listing the permissions (e.g., files.content.read, files.content.write).

After approval, you’ll get an authorization code (a short string) displayed on the screen or redirected to a URL if you specified a redirect URI.

## Exchange the Code for Tokens:
Use a tool like curl or a simple script to exchange the authorization code for an access token and refresh token. Run this command:  
If on windows open Ubuntu console with WSL and then run the following command.

```bash
curl https://api.dropbox.com/oauth2/token \
  -d code=<AUTHORIZATION_CODE> \
  -d grant_type=authorization_code \
  -d client_id=<YOUR_APP_KEY> \
  -d client_secret=<YOUR_APP_SECRET>
 ```

Replace <AUTHORIZATION_CODE> with the code from Step 2, and <YOUR_APP_KEY> and <YOUR_APP_SECRET> with your app’s credentials from the App Console.

You’ll get a JSON response like:
json

{
  "access_token": "sl.<short-lived-token>",
  "expires_in": 14400,
  "refresh_token": "<long-lived-refresh-token>",
  "token_type": "bearer",
  "scope": "files.content.read files.content.write",
  "uid": "<user-id>",
  "account_id": "dbid:<account-id>"
}

The access_token lasts 4 hours (14,400 seconds), but the refresh_token is what you want—it’s permanent until revoked.

Store the Refresh Token:
Save the refresh_token securely (e.g., in your config.json or an environment variable). For your current setup, you could update config.json to include it:

Example config.json
```json
{
  "syncPairs": [
    {
      "localPath": "C:\\GOG Games\\HoMM 3 Complete\\Games",
      "remotePath": "/GameSaves/HoMM3"
    },
    {
      "localPath": "E:\\GOG Galaxy\\Games\\HoMM 2 Gold\\cloud_saves\\GAMES",
      "remotePath": "/GameSaves/HoMM2"
    },
    {
      "localPath": "C:\\RetroArch-Win64\\config",
      "remotePath": "/GameSaves/RetroArch/config"
    },	
    {
      "localPath": "C:\\RetroArch-Win64\\states",
      "remotePath": "/GameSaves/RetroArch/states"
    },	
    {
      "localPath": "C:\\RetroArch-Win64\\saves",
      "remotePath": "/GameSaves/RetroArch/saves"
    }
  ],
  "cloudProvider": "Dropbox",
  "credentials": "token here",
"refreshToken": "refresh token here",
"appKey": "key here",
"appSecret": "secret here"
}
```