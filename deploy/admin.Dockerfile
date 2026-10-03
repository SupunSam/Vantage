# Admin Portal: build the React app, serve it with nginx, which also forwards /api to the API container.
# Build context is the repo root.
FROM node:22-alpine AS build
WORKDIR /web
COPY frontend/package.json frontend/package-lock.json ./
COPY frontend/shared/package.json shared/
COPY frontend/user-portal/package.json user-portal/
COPY frontend/admin-portal/package.json admin-portal/
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build -w admin-portal

FROM nginx:1.27-alpine
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /web/admin-portal/dist /usr/share/nginx/html
EXPOSE 80
