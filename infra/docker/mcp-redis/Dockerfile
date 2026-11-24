FROM mcp/redis:upstream

WORKDIR /app

# Preserve original main.py
RUN cp /app/src/main.py /app/src/main_orig.py

# Install our resilient wrapper as the primary main.py
COPY redis_mcp_wrapper.py /app/src/main.py

# Run directly via the pre-built virtual environment python with zero stdout pollution
CMD ["/app/.venv/bin/python", "src/main.py"]
