using Magic.Kernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SpaceDb.Services;

namespace SpaceDb.Controllers
{
    [Route("/api/v1/agi")]
    [ApiController]
    //[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
    public class AgiController
    {
        private readonly MagicKernel kernel;

        public AgiController(MagicKernel kernel)
        {
            this.kernel = kernel;
        }

        [HttpPost("execute")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public async Task Execute([FromBody] ExecuteInstruction request)
        {
            var result = await kernel.InterpreteSourceCodeAsync(request.Instruction);
        }

        public class ExecuteInstruction
        {
            public string Instruction { get; set; } = string.Empty;
        }
    }
}
