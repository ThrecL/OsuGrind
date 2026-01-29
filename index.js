export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsHeaders = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type"
    };
    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }
    if (url.pathname === "/ping" && request.method === "POST") {
      try {
        const { userId, username, version } = await request.json();
        if (!userId) return new Response("Missing userId", { status: 400, headers: corsHeaders });
        const now = Date.now();
        
        await env.DB.prepare(
          `INSERT INTO users (id, username, last_seen, version, created_at) 
           VALUES (?, ?, ?, ?, ?) 
           ON CONFLICT(id) DO UPDATE SET 
             username = CASE WHEN ? IS NOT NULL AND ? != '' THEN ? ELSE username END,
             last_seen = ?, 
             version = ?`
        ).bind(userId, username || null, now, version || "unknown", now, username, username, username, now, version || "unknown").run();
        
        return new Response(JSON.stringify({ status: "ok" }), {
          headers: { ...corsHeaders, "Content-Type": "application/json" }
        });
      } catch (e) {
        return new Response(JSON.stringify({ error: e.message }), { status: 500, headers: corsHeaders });
      }
    }
    if (url.pathname === "/stats" && request.method === "GET") {
      try {
          const fiveMinutesAgo = Date.now() - 5 * 60 * 1e3;
          
          const onlineResult = await env.DB.prepare(
            "SELECT COUNT(*) as count FROM users WHERE last_seen > ?"
          ).bind(fiveMinutesAgo).first();
          
          const totalResult = await env.DB.prepare(
            "SELECT COUNT(*) as count FROM users"
          ).first();
          
          return new Response(JSON.stringify({
            online: onlineResult.count || 0,
            total: totalResult.count || 0
          }), {
            headers: { ...corsHeaders, "Content-Type": "application/json" }
          });
      } catch (e) {
          return new Response(JSON.stringify({ error: "DB Error: " + e.message, stack: e.stack }), { 
              status: 500, 
              headers: { ...corsHeaders, "Content-Type": "application/json" } 
          });
      }
    }
    return new Response("Not Found", { status: 404, headers: corsHeaders });
  }
};
