Create a config.json file and place it in the same directoy as the executable. An example config.json is below, you'll need to update the localPath's to be whereever they would be on your system for the matching cloud folder, and the credentials is where you'll paste the dropbox token value


Example config.json
```json
{
  "syncPairs": [
    {
      "localPath": "E:\\GOG Galaxy\\Games\\HoMM 3 Complete\\Games",
      "remotePath": "/GameSaves/HoMM3"
    },
    {
      "localPath": "E:\\GOG Galaxy\\Games\\HoMM 2 Gold\\cloud_saves\\GAMES",
      "remotePath": "/GameSaves/HoMM2"
    },
    {
      "localPath": "C:\\RetroArch-Win64\\saves",
      "remotePath": "/GameSaves/RetroArch"
    }
  ],
  "cloudProvider": "Dropbox",
  "credentials": "token content here"
}
```