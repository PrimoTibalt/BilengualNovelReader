# Novel Reader for Android

A [Trusted Web Activity](https://developer.chrome.com/docs/android/trusted-web-activity): the
site, shown by Chrome full screen with no address bar, as an app with its own icon (D51).
Offline reading is the site's own — its service worker and the chapters downloaded from the
drawer's *offline reading* section — so a web deploy updates the app. This project only changes
when the address, the icon or the signing key does.

## Building

Needs JDK 17+, the Android SDK (platform 35, build-tools) and Gradle 8.9+.

```bash
export JAVA_HOME=…            # JDK 17 or later
export ANDROID_HOME=~/Android/Sdk
cd android
gradle assembleDebug          # app/build/outputs/apk/debug/app-debug.apk
gradle assembleRelease        # signed, once keystore.properties exists (below)
adb install -r app/build/outputs/apk/release/app-release.apk
```

## Signing, and the address bar

Chrome hides the address bar only when the site vouches for the key the APK is signed with:

1. Make a release key, once, and keep it safe — every update must be signed with it:
   ```bash
   keytool -genkeypair -v -keystore novelreader.jks -alias novelreader \
     -keyalg RSA -keysize 2048 -validity 10000
   ```
2. `android/keystore.properties` (gitignored):
   ```
   storeFile=novelreader.jks
   storePassword=…
   keyAlias=novelreader
   keyPassword=…
   ```
3. Its fingerprint goes into the server's `appsettings.json`,
   `TrustedWebActivity:Sha256CertFingerprints` (add the debug key's too, to test a debug build):
   ```bash
   keytool -list -v -keystore novelreader.jks -alias novelreader | grep SHA256
   ```
4. Android asks for `https://primotibalt.top/.well-known/assetlinks.json` — at the domain's
   root, not under `/reader`. The app serves it; nginx has to pass that one path through:
   ```nginx
   location = /.well-known/assetlinks.json {
       proxy_pass http://127.0.0.1:5000;
   }
   ```

Until all of that is in place the app still works, as a custom tab with the address bar showing.

## Things to know

- It runs in **Chrome**: the app shares Chrome's sign-in, storage and downloaded chapters for the
  site. Without Chrome on the phone it falls back to a custom tab in the default browser.
- The address is set in `app/build.gradle.kts` (`host`, `launchUrl`, `appId`), and must match
  `asset_statements` in `res/values/strings.xml` and `TrustedWebActivity:PackageName` on the server.
