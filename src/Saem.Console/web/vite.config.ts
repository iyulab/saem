import { defineConfig } from "vite";

// The console serves what this builds (../wwwroot). In development, `npm run dev` serves the UI and hands the
// console's own paths — the API, sign-in and sign-out, the issuer's callback — to a console running at SAEM_CONSOLE.
const console = process.env.SAEM_CONSOLE ?? "http://127.0.0.1:5080";

export default defineConfig({
  build: { outDir: "../wwwroot", emptyOutDir: true },
  server: {
    proxy: Object.fromEntries(["/api", "/login", "/logout", "/signin-oidc"].map((path) => [path, console])),
  },
});
