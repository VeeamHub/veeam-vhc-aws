FROM python:3.12-slim
WORKDIR /app
COPY pyproject.toml .
COPY vhc_monitor/ vhc_monitor/
RUN pip install --no-cache-dir .
ENTRYPOINT ["vhc-monitor"]
CMD ["all", "--config", "/config/config.yaml"]
