import type { Routes } from "@angular/router";

/**
 * The two pages. The paths are the server's: `/Login` and `/ReadingPage` are served by their
 * controllers (the second only to a signed-in reader), and each loads only its own code.
 */
export const routes: Routes = [
  { path: "Login", loadComponent: () => import("./login/login-page").then((m) => m.LoginPage) },
  { path: "ReadingPage", loadComponent: () => import("./reading/reading-page").then((m) => m.ReadingPage) },
  { path: "**", redirectTo: "ReadingPage" },
];
