import { bootstrapApplication } from '@angular/platform-browser';
import { appConfig } from './app/app.config';
import { App } from './app/app';
import { registerServiceWorker } from './app/core/service-worker';

bootstrapApplication(App, appConfig)
  .catch((err) => console.error(err));

registerServiceWorker();
