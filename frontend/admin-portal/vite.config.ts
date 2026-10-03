import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The API runs on http://localhost:5080 (dotnet run in backend/src/Vantage.Api); requests to /api are proxied there.
export default defineConfig({
  plugins: [react()],
  server: { port: 5174, strictPort: true, proxy: { "/api": "http://localhost:5080" } },
});
