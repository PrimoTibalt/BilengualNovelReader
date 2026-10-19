import { provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from "@angular/core";
import { bootstrapApplication } from "@angular/platform-browser";
import { AdminPage } from "./app/admin-page";

bootstrapApplication(AdminPage, {
  providers: [provideBrowserGlobalErrorListeners(), provideZonelessChangeDetection()],
}).catch((error: unknown) => console.error(error));
